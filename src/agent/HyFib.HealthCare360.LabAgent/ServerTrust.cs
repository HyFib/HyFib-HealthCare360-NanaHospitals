using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.WindowsInterop.Trust;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>
/// Pins the server to the hospital's private CA, with the same rules as HC360 Desktop (SRS DED-04, DED-05): the server's
/// certificate must chain to exactly this CA, or, while the hospital CA is rolled over, to the next CA the agent holds or the
/// one the pinned CA replaced, and name the host the agent contacts. The Windows trust store is not consulted, so a device on the network cannot pose as the server with
/// some other valid certificate. Every call the agent makes, the relay's WebSocket included, goes through this check; none
/// ever accepts any certificate. The check itself is <see cref="HospitalCaPins"/>, which HC360 Desktop shares.
/// </summary>
internal sealed class ServerTrust : IDisposable
{
    public ServerTrust(string caPem, string? previousCaPem = null, string? nextCaPem = null)
    {
        Pins = new HospitalCaPins(caPem, previousCaPem, nextCaPem);
    }

    /// <summary>
    /// The CAs pinned: the agent holds the next CA on the pinned one's word, and switches to it with the server
    /// (<see cref="HospitalCaFollow"/>).
    /// </summary>
    public HospitalCaPins Pins { get; }

    /// <summary>SHA-256 of the DER certificate as upper-case hex (the format the server installer prints).</summary>
    public string Fingerprint => Pins.Fingerprint;

    public static string ComputeFingerprint(X509Certificate2 certificate) => HospitalCaPins.ComputeFingerprint(certificate);

    /// <summary>"AB12 CD34 …", "ab12:cd34:…" or "AB12CD34…" → "AB12CD34…"; null when it is not a SHA-256 fingerprint.</summary>
    public static string? NormalizeFingerprint(string? value)
    {
        var hex = new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray()).ToUpperInvariant();
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigitUpper) ? hex : null;
    }

    /// <summary>
    /// Whether <paramref name="serverCertificate"/> may be trusted for <paramref name="host"/>: it names the host,
    /// is for a TLS server, and chains to exactly a pinned CA, directly or through <paramref name="intermediates"/>
    /// (the CA certificates the server sent in the handshake).
    /// </summary>
    public bool IsTrusted(X509Certificate2 serverCertificate, string host, X509Certificate2Collection? intermediates = null) =>
        Pins.IsTrusted(serverCertificate, host, intermediates);

    /// <summary>The TLS callback's check (<see cref="HospitalCaPins.Validate"/>).</summary>
    public bool Validate(X509Certificate? certificate, X509Chain? handshake, string host) => Pins.Validate(certificate, handshake, host);

    /// <summary>Every certificate of the handshake other than the server's own.</summary>
    internal static X509Certificate2Collection Intermediates(X509Chain? handshake, X509Certificate2 server) =>
        HospitalCaPins.Intermediates(handshake, server);

    /// <summary>HttpClient for the agent's calls: pinned, no proxy (the server is on the hospital LAN), bounded waits.</summary>
    public HttpClient CreateHttpClient(Uri baseAddress, TimeSpan timeout)
    {
        var host = baseAddress.Host;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, _) => Validate(certificate, chain, host),
            },
        };
        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = timeout };
    }

    /// <summary>The same pinning for the relay's WebSocket.</summary>
    public void ApplyTo(ClientWebSocketOptions options, string host)
    {
        options.Proxy = null;
        options.RemoteCertificateValidationCallback = (_, certificate, chain, _) => Validate(certificate, chain, host);
    }

    public void Dispose() => Pins.Dispose();
}
