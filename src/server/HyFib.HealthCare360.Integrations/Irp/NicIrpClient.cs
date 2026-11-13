using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Integrations.Irp;

/// <summary>
/// The NIC e-invoice API v1.03, called directly (<c>nic</c>: encrypted as NIC requires, <see cref="NicIrpCrypto"/>) or
/// through a GST suvidha provider that passes the same API on (<c>gsp</c>: plain JSON unless <see cref="IrpSettings.Encrypt"/>).
/// Every call carries the headers NIC names (<c>client_id</c>, <c>client_secret</c>, <c>Gstin</c>, <c>user_name</c>,
/// <c>AuthToken</c>); the token and session key are kept per facility, GSTIN and credential until a few minutes before
/// they expire, and asked for again once when the portal says the token is no longer valid. An IRN asked for twice is
/// answered by the portal as a duplicate (2150) with the IRN it holds, so a retried request never makes two.
/// </summary>
internal sealed class NicIrpClient(HttpClient http, IntegrationTrace trace, TimeProvider time) : IIrpProvider
{
    private static readonly Dictionary<string, (string Token, byte[]? SessionKey, DateTimeOffset ExpiresAt)> Sessions = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public IReadOnlyCollection<string> Keys { get; } = [ProviderKeys.Nic, ProviderKeys.Gsp];

    public async Task<IrpResult<IrpIrn>> GenerateAsync(IrpContext context, JsonObject payload, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(context, HttpMethod.Post, context.Settings.InvoicePath, payload, "irn.generate", cancellationToken);
        return answer.Error is { } error ? new IrpResult<IrpIrn>(null, error) : IrpAnswers.Irn(answer.Value!);
    }

    public async Task<IrpResult<IrpCancellation>> CancelAsync(IrpContext context, string irn, int reason, string remark, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["Irn"] = irn, ["CnlRsn"] = reason.ToString(CultureInfo.InvariantCulture), ["CnlRem"] = remark };
        var answer = await CallAsync(context, HttpMethod.Post, context.Settings.InvoicePath.TrimEnd('/') + "/Cancel", body, "irn.cancel", cancellationToken);
        if (answer.Error is { } error)
        {
            return new IrpResult<IrpCancellation>(null, error);
        }

        return IrpAnswers.Time(answer.Value!["CancelDate"]) is { } at
            ? IrpResult<IrpCancellation>.Ok(new IrpCancellation(IrpAnswers.Text(answer.Value!["Irn"]) ?? irn, at))
            : IrpResult<IrpCancellation>.Fail(IrpError.Unreachable, "The portal's answer did not say when the IRN was cancelled.");
    }

    public async Task<IrpResult<IrpIrn>> GetByIrnAsync(IrpContext context, string irn, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(context, HttpMethod.Get, $"{context.Settings.InvoicePath.TrimEnd('/')}/irn/{Uri.EscapeDataString(irn)}", null, "irn.get",
            cancellationToken);
        return answer.Error is { } error ? new IrpResult<IrpIrn>(null, error) : IrpAnswers.Irn(answer.Value!);
    }

    public async Task<IrpResult<IrpEwayBill>> GenerateEwayBillAsync(IrpContext context, JsonObject request, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(context, HttpMethod.Post, context.Settings.EwayBillPath, request, "ewaybill.generate", cancellationToken);
        return answer.Error is { } error ? new IrpResult<IrpEwayBill>(null, error) : IrpAnswers.EwayBill(answer.Value!);
    }

    /// <summary>
    /// The e-way bill the portal holds for an IRN (GET <c>ewaybill/irn/{irn}</c> under the e-way bill path): <c>EwbNo</c>,
    /// <c>Status</c>, <c>EwbDt</c> and <c>EwbValidTill</c>, or 4005 when it holds none. The path and answer are as GSP
    /// documentation of the NIC API gives them, not yet run against NIC's sandbox.
    /// </summary>
    public async Task<IrpResult<IrpEwayBill>> GetEwayBillAsync(IrpContext context, string irn, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(context, HttpMethod.Get, $"{context.Settings.EwayBillPath.TrimEnd('/')}/irn/{Uri.EscapeDataString(irn)}", null,
            "ewaybill.get", cancellationToken);
        return answer.Error is { } error ? new IrpResult<IrpEwayBill>(null, error) : IrpAnswers.EwayBill(answer.Value!);
    }

    private async Task<(JsonObject? Value, IrpError? Error)> CallAsync(
        IrpContext context, HttpMethod method, string path, JsonObject? body, string operation, CancellationToken cancellationToken)
    {
        if (Problem(context) is { } problem)
        {
            return (null, new IrpError(IrpError.NotConnected, problem));
        }

        for (var attempt = 0; ; attempt++)
        {
            var session = await SessionAsync(context, cancellationToken);
            if (session.Error is { } authError)
            {
                return (null, authError);
            }

            var (token, sessionKey) = session.Value;
            var started = trace.Start();
            using var request = new HttpRequestMessage(method, Endpoint(context, path));
            Headers(request, context);
            request.Headers.TryAddWithoutValidation("AuthToken", token);
            if (body is not null)
            {
                var json = body.ToJsonString();
                var content = sessionKey is null ? json : new JsonObject { ["Data"] = NicIrpCrypto.EncryptPayload(json, sessionKey) }.ToJsonString();
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }

            string text;
            int status;
            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
            {
                var reason = IntegrationErrors.Describe(ex, "The e-invoice portal");
                await trace.LogAsync(IntegrationNames.Irp, Provider(context), operation, false, started, path, path, error: reason, facilityId: context.FacilityId);
                return (null, new IrpError(IrpError.Unreachable, reason));
            }

            var read = Read(text, sessionKey);
            var ok = read.Error is null;
            await trace.LogAsync(IntegrationNames.Irp, Provider(context), operation, ok, started, path, path,
                string.Create(CultureInfo.InvariantCulture, $"HTTP {status}"), ok ? null : $"{read.Error!.Code}: {read.Error.Message}",
                facilityId: context.FacilityId);

            // 1005: the token is not valid (expired or replaced by a newer one): ask for a new one, once.
            if (read.Error?.Code == "1005" && attempt == 0)
            {
                Forget(context);
                continue;
            }

            return read;
        }
    }

    /// <summary>Reads an answer: <c>Status</c> 1 with <c>Data</c> (encrypted, plain text or an object), or 0 with its errors.</summary>
    internal static (JsonObject? Value, IrpError? Error) Read(string text, byte[]? sessionKey)
    {
        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            answer = null;
        }

        if (answer is null)
        {
            return (null, new IrpError(IrpError.Unreachable, "The e-invoice portal's answer could not be read."));
        }

        if (IrpAnswers.Text(answer["Status"]) != "1")
        {
            return (null, IrpAnswers.Error(answer));
        }

        try
        {
            return answer["Data"] switch
            {
                JsonObject data => (data, null),
                JsonValue value when value.ToString() is { } data => (JsonNode.Parse(sessionKey is null ? data : NicIrpCrypto.DecryptPayload(data, sessionKey)) as JsonObject, null),
                _ => (null, new IrpError(IrpError.Unreachable, "The e-invoice portal's answer held no data.")),
            };
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or CryptographicException)
        {
            return (null, new IrpError(IrpError.Unreachable, "The e-invoice portal's answer could not be decrypted or read."));
        }
    }

    private async Task<(IrpError? Error, (string Token, byte[]? SessionKey) Value)> SessionAsync(IrpContext context, CancellationToken cancellationToken)
    {
        var key = SessionKey(context);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Sessions.TryGetValue(key, out var cached) && cached.ExpiresAt > time.GetUtcNow())
            {
                return (null, (cached.Token, cached.SessionKey));
            }

            var appKey = NicIrpCrypto.NewAppKey();
            var credentials = new JsonObject
            {
                ["UserName"] = context.Secrets.UserName,
                ["Password"] = context.Secrets.Password,
                ["AppKey"] = Convert.ToBase64String(appKey),
                ["ForceRefreshAccessToken"] = false,
            }.ToJsonString();
            var encrypt = context.Settings.Encrypts;
            var started = trace.Start();
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(context, context.Settings.AuthPath));
            Headers(request, context);
            request.Content = new StringContent(
                encrypt ? new JsonObject { ["Data"] = NicIrpCrypto.EncryptWithPublicKey(credentials, context.Secrets.PublicKeyPem!) }.ToJsonString() : credentials,
                Encoding.UTF8, "application/json");
            string text;
            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
            {
                var reason = IntegrationErrors.Describe(ex, "The e-invoice portal");
                await trace.LogAsync(IntegrationNames.Irp, Provider(context), "auth", false, started, error: reason, facilityId: context.FacilityId);
                return (new IrpError(IrpError.Unreachable, reason), default);
            }

            // The authentication answer is never encrypted as a whole; its Sek is, under the app key.
            var read = Read(text, null);
            await trace.LogAsync(IntegrationNames.Irp, Provider(context), "auth", read.Error is null, started,
                error: read.Error is { } e ? $"{e.Code}: {e.Message}" : null, facilityId: context.FacilityId);
            if (read.Error is { } error)
            {
                return (error, default);
            }

            var data = read.Value!;
            if (IrpAnswers.Text(data["AuthToken"]) is not { } token)
            {
                return (new IrpError(IrpError.Unreachable, "The e-invoice portal gave no authentication token."), default);
            }

            byte[]? sessionKey = null;
            if (encrypt)
            {
                try
                {
                    sessionKey = NicIrpCrypto.DecryptSessionKey(IrpAnswers.Text(data["Sek"]) ?? string.Empty, appKey);
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    return (new IrpError(IrpError.Unreachable, "The e-invoice portal's session key could not be decrypted."), default);
                }
            }

            // NIC tokens last six hours; renewed ten minutes early.
            var expires = IrpAnswers.Time(data["TokenExpiry"]) ?? time.GetUtcNow().AddHours(6);
            Sessions[key] = (token, sessionKey, expires.AddMinutes(-10));
            return (null, (token, sessionKey));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? Problem(IrpContext context)
    {
        var secrets = context.Secrets;
        if (string.IsNullOrWhiteSpace(secrets.ClientId) || string.IsNullOrWhiteSpace(secrets.ClientSecret)
            || string.IsNullOrWhiteSpace(secrets.UserName) || string.IsNullOrWhiteSpace(secrets.Password))
        {
            return "The e-invoice portal's client id, client secret, API user and password are not all set. Enter them in Administration → Integrations.";
        }

        if (context.Settings.Encrypts && string.IsNullOrWhiteSpace(secrets.PublicKeyPem))
        {
            return "Enter the e-invoice portal's public key (PEM) in Administration → Integrations: NIC encrypts with it.";
        }

        return IntegrationHttp.TryCreateEndpoint(context.Settings.BaseUrl, requireHttps: true, out _)
            ? null
            : "The e-invoice portal's address must be an https address.";
    }

    private static Uri Endpoint(IrpContext context, string path)
    {
        IntegrationHttp.TryCreateEndpoint(context.Settings.BaseUrl, requireHttps: true, out var baseUri);
        return IntegrationHttp.Combine(baseUri!, path.TrimStart('/'));
    }

    private static void Headers(HttpRequestMessage request, IrpContext context)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("client_id", context.Secrets.ClientId);
        request.Headers.TryAddWithoutValidation("client_secret", context.Secrets.ClientSecret);
        request.Headers.TryAddWithoutValidation("Gstin", context.Gstin);
        request.Headers.TryAddWithoutValidation("user_name", context.Secrets.UserName);
    }

    private static string Provider(IrpContext context) => context.Settings.Provider?.Trim().ToLowerInvariant() ?? ProviderKeys.Nic;

    /// <summary>The session's key: facility, GSTIN and credential, hashed so the user name is not kept in memory structures.</summary>
    private static string SessionKey(IrpContext context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.FacilityId}|{context.Gstin}|{context.Settings.BaseUrl}|{context.Secrets.ClientId}|{context.Secrets.UserName}")));

    private static void Forget(IrpContext context)
    {
        Gate.Wait();
        try
        {
            Sessions.Remove(SessionKey(context));
        }
        finally
        {
            Gate.Release();
        }
    }
}
