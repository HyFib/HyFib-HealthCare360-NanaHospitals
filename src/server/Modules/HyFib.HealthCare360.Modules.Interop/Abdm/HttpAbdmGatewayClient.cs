using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// The gateway session (an access token from the client id and secret), shared by every call until shortly before it
/// expires. The sandbox issues it for twenty minutes; NHA's wrapper renews it after fifteen, and so does HC360, or
/// sooner when the gateway says it expires sooner.
/// </summary>
internal sealed class AbdmGatewaySessions
{
    /// <summary>How long before the gateway's expiry the session is renewed.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    /// <summary>The session's life when the gateway does not say (the sandbox's twenty minutes).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _current;

    /// <summary>The session for these credentials if it is still good at <paramref name="now"/>, or a new one.</summary>
    public async Task<Result<string>> GetAsync(string key, DateTimeOffset now, Func<CancellationToken, Task<Result<(string Token, TimeSpan Lifetime)>>> create, CancellationToken ct)
    {
        if (_current is { } session && session.Key == key && now < session.RenewAt)
        {
            return session.Token;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_current is { } again && again.Key == key && now < again.RenewAt)
            {
                return again.Token;
            }

            var created = await create(ct);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            var (token, lifetime) = created.Value;
            var renewAt = now + (lifetime > Margin * 2 ? lifetime - Margin : lifetime / 2);
            _current = new Session(key, token, renewAt);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the session, when the gateway no longer accepts it.</summary>
    public void Forget(string token)
    {
        if (_current?.Token == token)
        {
            _current = null;
        }
    }

    private sealed record Session(string Key, string Token, DateTimeOffset RenewAt);
}

/// <summary>
/// The live gateway client (<see cref="IAbdmGatewayClient"/>): the session, HIP-initiated linking, the answers of
/// user-initiated linking, and consent and data flow. Every call goes
/// to the base URL in the settings with the headers NHA's wrapper sends (<c>Authorization: Bearer</c>, <c>X-CM-ID</c>,
/// <c>REQUEST-ID</c>, <c>TIMESTAMP</c>, <c>X-HIP-ID</c>, and <c>X-LINK-TOKEN</c> when adding care contexts). The gateway
/// accepts a call with 202 and answers it later through HC360's callback endpoints; any other answer is a refusal,
/// with the gateway's error code and message. A call is never retried here: the gateway may have acted on it when an
/// answer is lost, so the request is marked failed and staff send it again. A session the gateway no longer accepts
/// (401) is renewed once.
/// </summary>
internal sealed class HttpAbdmGatewayClient(
    HttpClient http,
    AbdmGatewayConfiguration configuration,
    DisabledAbdmGatewayClient refusal,
    AbdmGatewaySessions sessions,
    TimeProvider time,
    ILogger<HttpAbdmGatewayClient> log) : IAbdmGatewayClient
{
    public const string SessionPath = "/gateway/v3/sessions";
    public const string GenerateTokenPath = "/v3/token/generate-token";
    public const string AddCareContextsPath = "/hip/v3/link/carecontext";
    public const string OnDiscoverPath = "/user-initiated-linking/v3/patient/care-context/on-discover";
    public const string OnLinkInitPath = "/user-initiated-linking/v3/link/care-context/on-init";
    public const string OnLinkConfirmPath = "/user-initiated-linking/v3/link/care-context/on-confirm";
    public const string OnConsentNotifyPath = "/consent/v3/request/hip/on-notify";
    public const string OnHealthInformationRequestPath = "/data-flow/v3/health-information/hip/on-request";
    public const string TransferNotifyPath = "/data-flow/v3/health-information/notify";
    public const string OnSharePath = "/patient-share/v3/on-share";

    public const string RefusedCode = "abdm.gateway_refused";
    public const string UnreachableCode = "abdm.gateway_unreachable";

    /// <summary>Gateway answers are small; anything larger is not read.</summary>
    private const int MaxErrorBodyChars = 4096;

    public async Task<Result<string>> CreateSessionAsync(CancellationToken ct)
    {
        var setUp = await SetUpAsync(ct);
        return setUp.IsFailure ? setUp.Error! : await SessionAsync(setUp.Value, ct);
    }

    public async Task<Result> RequestLinkTokenAsync(AbdmLinkTokenRequest request, Guid requestId, CancellationToken ct) =>
        await SendAsync(
            GenerateTokenPath,
            new AbdmGatewayMessages.GenerateTokenRequest(request.AbhaAddress, request.Name, request.Gender, request.YearOfBirth),
            requestId,
            linkToken: null,
            "ask for a link token",
            ct);

    public async Task<Result> LinkCareContextsAsync(AbdmCareContextLink link, string linkToken, Guid requestId, CancellationToken ct)
    {
        // One group per kind of record, as NHA's wrapper sends them: the care contexts of the group, its count, and the
        // health-information type the consent manager offers them under.
        var groups = AbdmGatewayMessages.Groups(link.PatientId, link.PatientDisplay, link.CareContexts.Select(c => (c.Reference, c.Display, c.Type)));
        return await SendAsync(
            AddCareContextsPath,
            new AbdmGatewayMessages.AddCareContextsRequest(link.AbhaNumber, link.AbhaAddress, groups),
            requestId,
            linkToken,
            "link the records to the patient's ABHA",
            ct);
    }

    public Task<Result> AnswerDiscoveryAsync(AbdmGatewayMessages.OnDiscover answer, CancellationToken ct) =>
        SendAsync(OnDiscoverPath, answer, Guid.CreateVersion7(), linkToken: null, "answer a patient's search for their records", ct);

    public Task<Result> AnswerLinkInitAsync(AbdmGatewayMessages.OnLinkInit answer, CancellationToken ct) =>
        SendAsync(OnLinkInitPath, answer, Guid.CreateVersion7(), linkToken: null, "answer a patient's request to link records", ct);

    public Task<Result> AnswerLinkConfirmAsync(AbdmGatewayMessages.OnLinkConfirm answer, CancellationToken ct) =>
        SendAsync(OnLinkConfirmPath, answer, Guid.CreateVersion7(), linkToken: null, "confirm the records the patient linked", ct);

    public Task<Result> AnswerConsentNotifyAsync(AbdmGatewayMessages.OnConsentNotify answer, CancellationToken ct) =>
        SendAsync(OnConsentNotifyPath, answer, Guid.CreateVersion7(), linkToken: null, "acknowledge a consent", ct);

    public Task<Result> AnswerHealthInformationRequestAsync(AbdmGatewayMessages.OnHealthInformationRequest answer, CancellationToken ct) =>
        SendAsync(OnHealthInformationRequestPath, answer, Guid.CreateVersion7(), linkToken: null, "answer a request for health information", ct);

    public Task<Result> NotifyTransferAsync(AbdmGatewayMessages.TransferNotify notification, CancellationToken ct) =>
        SendAsync(TransferNotifyPath, notification, Guid.CreateVersion7(), linkToken: null, "report a transfer of health information", ct);

    public Task<Result> AnswerShareAsync(AbdmGatewayMessages.OnShare answer, CancellationToken ct) =>
        SendAsync(OnSharePath, answer, Guid.CreateVersion7(), linkToken: null, "give a token for a shared profile", ct);

    private async Task<Result> SendAsync(string path, object body, Guid requestId, string? linkToken, string what, CancellationToken ct)
    {
        var setUp = await SetUpAsync(ct);
        if (setUp.IsFailure)
        {
            return setUp.Error!;
        }

        var gateway = setUp.Value;
        for (var attempt = 0; ; attempt++)
        {
            var session = await SessionAsync(gateway, ct);
            if (session.IsFailure)
            {
                return session.Error!;
            }

            using var request = Request(gateway, path, body, requestId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Value);
            request.Headers.Add("X-HIP-ID", gateway.Settings.HipId);
            if (linkToken is not null)
            {
                request.Headers.Add("X-LINK-TOKEN", linkToken);
            }

            var answer = await ExchangeAsync(gateway, request, what, ct);
            if (answer.IsFailure)
            {
                return answer.Error!;
            }

            using var response = answer.Value;
            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                return Result.Success();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                // The gateway ended the session early: a new one, and the call once more (it was refused, not acted on).
                sessions.Forget(session.Value);
                continue;
            }

            return await RefusedAsync(response, what, ct);
        }
    }

    private async Task<Result<string>> SessionAsync(GatewaySetUp gateway, CancellationToken ct)
    {
        var key = $"{gateway.BaseUrl}|{gateway.Credentials.ClientId}";
        return await sessions.GetAsync(key, time.GetUtcNow(), async innerCt =>
        {
            using var request = Request(
                gateway,
                SessionPath,
                new AbdmGatewayMessages.SessionRequest(gateway.Credentials.ClientId!, gateway.Credentials.ClientSecret!),
                Guid.CreateVersion7());
            var answer = await ExchangeAsync(gateway, request, "start a gateway session", innerCt);
            if (answer.IsFailure)
            {
                return answer.Error!;
            }

            using var response = answer.Value;
            if (!response.IsSuccessStatusCode)
            {
                return (await RefusedAsync(response, "start a gateway session", innerCt)).Error!;
            }

            AbdmGatewayMessages.SessionResponse? session;
            try
            {
                session = await response.Content.ReadFromJsonAsync<AbdmGatewayMessages.SessionResponse>(AbdmGatewayMessages.Json, innerCt);
            }
            catch (JsonException)
            {
                session = null;
            }

            if (string.IsNullOrWhiteSpace(session?.AccessToken))
            {
                return Error.Rule(RefusedCode, "The ABDM gateway started no session: its answer had no access token.");
            }

            var lifetime = session.ExpiresIn is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : AbdmGatewaySessions.DefaultLifetime;
            return (session.AccessToken, lifetime);
        }, ct);
    }

    private HttpRequestMessage Request(GatewaySetUp gateway, string path, object body, Guid requestId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(gateway.BaseUrl + path))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), AbdmGatewayMessages.Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CM-ID", gateway.Settings.CmId);
        request.Headers.Add("REQUEST-ID", requestId.ToString("D"));
        request.Headers.Add("TIMESTAMP", Timestamp(time.GetUtcNow()));
        return request;
    }

    /// <summary>The gateway's timestamp: an ISO 8601 instant in UTC with milliseconds, as NHA's wrapper writes it.</summary>
    public static string Timestamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private async Task<Result<HttpResponseMessage>> ExchangeAsync(GatewaySetUp gateway, HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(gateway.Settings.TimeoutSeconds, 5, 120)));
        try
        {
            return await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "The ABDM gateway could not be reached to {What}", what);
            return Error.Rule(UnreachableCode, $"The ABDM gateway could not be reached to {what}. Nothing was changed; try again in a moment.");
        }
    }

    private async Task<Result> RefusedAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        var error = AbdmGatewayMessages.ReadError(text.Length > MaxErrorBodyChars ? text[..MaxErrorBodyChars] : text);
        var detail = error is null ? $"HTTP {(int)response.StatusCode}"
            : string.Join(" ", new[] { error.Code, error.Message }.Where(s => !string.IsNullOrWhiteSpace(s)));
        log.LogWarning("The ABDM gateway refused to {What}: {Status} {Detail}", what, (int)response.StatusCode, detail);
        return Error.Rule(RefusedCode, $"The ABDM gateway refused to {what}: {detail}.");
    }

    private async Task<Result<GatewaySetUp>> SetUpAsync(CancellationToken ct)
    {
        if (await refusal.WhyNotAsync(ct) is { } refused)
        {
            return refused;
        }

        var (settings, credentials, _) = await configuration.ReadAsync(ct);
        return new GatewaySetUp(settings, credentials, settings.BaseUrl!.TrimEnd('/'));
    }

    private sealed record GatewaySetUp(AbdmGatewaySettings Settings, AbdmGatewayCredentials Credentials, string BaseUrl);
}
