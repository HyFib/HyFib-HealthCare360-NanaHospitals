using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HyFib.HealthCare360.WindowsInterop.Trust;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>The outcome of a heartbeat: the agent's analysers, or that the server no longer knows the agent.</summary>
internal sealed record HeartbeatOutcome(Assignments? Assignments, bool NotEnrolled);

/// <summary>The few calls the agent makes to the HC360 server, all over the pinned connection (<see cref="ServerTrust"/>).</summary>
internal sealed class ServerApi(Uri baseUri, ServerTrust trust) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = trust.CreateHttpClient(baseUri, TimeSpan.FromSeconds(30));

    public static string AppVersion { get; } =
        typeof(ServerApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public Task<SystemInfo?> GetSystemInfoAsync(CancellationToken cancellationToken) =>
        _http.GetFromJsonAsync<SystemInfo>(AgentProtocol.SystemInfoPath, Json, cancellationToken);

    public async Task<EnrolResult> EnrolAsync(string code, string name, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(AgentProtocol.EnrolPath,
            new EnrolRequest(code, name, Environment.MachineName, Environment.OSVersion.VersionString, AppVersion), Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentProblemException(await ProblemAsync(response, cancellationToken));
        }

        return await response.Content.ReadFromJsonAsync<EnrolResult>(Json, cancellationToken)
               ?? throw new AgentProblemException("The server returned an empty enrolment answer.");
    }

    /// <summary>Reports the COM ports and gets the analysers to relay. Throws HttpRequestException when the server cannot be reached.</summary>
    public async Task<HeartbeatOutcome> HeartbeatAsync(Guid deviceId, string secret, HeartbeatRequest report, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentProtocol.HeartbeatPath) { Content = JsonContent.Create(report, options: Json) };
        request.Headers.Add(AgentProtocol.DeviceIdHeader, deviceId.ToString());
        request.Headers.Add(AgentProtocol.DeviceKeyHeader, secret);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new HeartbeatOutcome(null, NotEnrolled: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(await ProblemAsync(response, cancellationToken), null, response.StatusCode);
        }

        return new HeartbeatOutcome(await response.Content.ReadFromJsonAsync<Assignments>(Json, cancellationToken), NotEnrolled: false);
    }

    /// <summary>
    /// After the hospital CA was rolled over, the new CA on the word of the CA this agent pins
    /// (<see cref="HospitalCaPins.SuccessorIn"/>): the server's CA certificate, fetched over a pinned connection that keeps
    /// what the server sent with its own certificate. Null when the server's CA is the pinned one, or the pinned CA did not
    /// vouch for it.
    /// </summary>
    public async Task<HospitalCaSuccessor?> FindCaSuccessorAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        X509Certificate2[] sent = [];
        using var http = trust.CreateHttpClient(baseUri, TimeSpan.FromSeconds(30), handshake => sent = handshake);
        var pem = await http.GetStringAsync(AgentProtocol.CaCertificatePath, cancellationToken);
        try
        {
            return trust.Pins.SuccessorIn(pem, sent, now);
        }
        finally
        {
            foreach (var certificate in sent)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// First contact with a server not yet trusted: download its public CA certificate. The TLS certificate cannot be
    /// checked yet, so the caller trusts the result only when its fingerprint equals the one IT gave at installation.
    /// </summary>
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "Trust on first use: the downloaded CA is used only if its fingerprint equals the one IT passed to the installer (EnrolCommand).")]
    public static async Task<(string Pem, string Fingerprint)> FetchCaCertificateAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
        var pem = await http.GetStringAsync(AgentProtocol.CaCertificatePath, cancellationToken);
        using var certificate = X509Certificate2.CreateFromPem(pem);
        if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority))
        {
            throw new AgentProblemException("The server did not return a CA certificate.");
        }

        return (pem, ServerTrust.ComputeFingerprint(certificate));
    }

    public void Dispose() => _http.Dispose();

    private static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (title is not null || detail is not null)
            {
                return string.Join(" ", new[] { title, detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }
        catch (JsonException)
        {
            // Not a problem-details body.
        }

        return $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.";
    }
}

/// <summary>A problem the server described, to show the person installing the agent as it is.</summary>
internal sealed class AgentProblemException(string message) : Exception(message);
