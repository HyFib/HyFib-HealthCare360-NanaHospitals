using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HyFib.HealthCare360.Desktop.Services;

internal sealed record SystemInfo(string Product, string Version, string ServerName, string? FacilityName, string? FacilityCode, string? CaFingerprint, DateTimeOffset ServerTime, int ApiVersion);

internal sealed record EnrollResult(Guid DeviceId, string DeviceSecret, string ServerName, string? CaFingerprint);

internal enum HeartbeatOutcome
{
    Ok,
    Revoked,
    Unreachable,
}

/// <summary>Minimal, pinned HTTPS client for the few calls the shell makes itself (the web app does the rest).</summary>
internal sealed class ServerClient(Uri baseUri, ServerTrust trust) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = trust.CreateHttpClient(baseUri, TimeSpan.FromSeconds(15));

    public Uri BaseUri => baseUri;

    public async Task<SystemInfo?> GetSystemInfoAsync(CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<SystemInfo>("api/v1/system/info", Json, cancellationToken);

    /// <summary>Round-trip time to the server's liveness endpoint, or null when unreachable.</summary>
    public async Task<TimeSpan?> PingAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync("health/live", timeout.Token);
            return response.IsSuccessStatusCode ? stopwatch.Elapsed : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<EnrollResult> EnrollAsync(string code, string deviceName, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/v1/devices/enroll",
            new { code, name = deviceName, machineName = Environment.MachineName, osVersion = Environment.OSVersion.VersionString, appVersion = DesktopSettings.AppVersion },
            Json,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ServerProblemException(await ReadProblemAsync(response, cancellationToken));
        }

        return await response.Content.ReadFromJsonAsync<EnrollResult>(Json, cancellationToken)
               ?? throw new ServerProblemException("The server returned an empty enrolment response.");
    }

    public async Task<HeartbeatOutcome> HeartbeatAsync(Guid deviceId, string deviceSecret, string? wifiSsid, int? wifiSignal, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "api/v1/devices/heartbeat",
                new { deviceId, deviceSecret, appVersion = DesktopSettings.AppVersion, wifiSsid, wifiSignal },
                Json,
                cancellationToken);
            return response.StatusCode == HttpStatusCode.Unauthorized ? HeartbeatOutcome.Revoked
                : response.IsSuccessStatusCode ? HeartbeatOutcome.Ok
                : HeartbeatOutcome.Unreachable;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HeartbeatOutcome.Unreachable;
        }
    }

    /// <summary>
    /// First contact with a server that is not yet trusted: download its public CA certificate. The TLS certificate
    /// cannot be verified yet, so the result is only accepted after its fingerprint matches the one advertised over
    /// mDNS / set by IT policy, or the one an administrator confirms on screen.
    /// </summary>
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation", Justification = "Trust on first use: the CA is not trusted until its fingerprint matches IT policy or an administrator confirms it (SetupWindow).")]
    public static async Task<(string Pem, string Fingerprint)> FetchCaCertificateAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        var pem = await http.GetStringAsync("api/v1/system/ca-certificate", cancellationToken);
        using var certificate = X509Certificate2.CreateFromPem(pem);
        if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority))
        {
            throw new ServerProblemException("The server did not return a CA certificate.");
        }

        return (pem, ServerTrust.ComputeFingerprint(certificate));
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString()!;
            }

            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                return title.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not a problem-details body.
        }

        return $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ServerProblemException(string message) : Exception(message);
