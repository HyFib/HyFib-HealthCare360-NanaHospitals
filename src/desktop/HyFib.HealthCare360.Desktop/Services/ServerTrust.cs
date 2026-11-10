using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.WindowsInterop.Trust;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// A certificate a pinned connection refused: the server presented it, and it is not from the pinned CA.
/// </summary>
internal sealed record RejectedCertificate(string Host, string Subject, string Issuer, string Fingerprint)
{
    public static RejectedCertificate From(X509Certificate2 certificate, string host) =>
        new(host, certificate.Subject, certificate.Issuer, ServerTrust.ComputeFingerprint(certificate));
}

/// <summary>
/// Certificate pinning to the hospital's private CA (SRS DED-04/SEC-06). The server's LAN certificate must chain to exactly
/// this CA, or, while the hospital CA is rolled over, to the next CA this workstation holds or the one the pinned CA replaced,
/// and name the host being contacted; the public Windows trust store is not consulted, so a device on the WiFi cannot
/// impersonate the server with some other valid certificate. The check itself is <see cref="HospitalCaPins"/>, which the lab
/// agent shares.
/// </summary>
internal sealed class ServerTrust : IDisposable
{
    public ServerTrust(string caPem, string? previousCaPem = null, string? nextCaPem = null)
    {
        Pins = new HospitalCaPins(caPem, previousCaPem, nextCaPem);
    }

    /// <summary>
    /// The CAs pinned: this workstation holds the next CA on the pinned one's word, and switches to it with the server
    /// (<see cref="HospitalCaFollow"/>).
    /// </summary>
    public HospitalCaPins Pins { get; }

    /// <summary>SHA-256 of the pinned CA as upper-case hex (same format the server advertises).</summary>
    public string Fingerprint => Pins.Fingerprint;

    /// <summary>
    /// Raised, on the connection's thread, when a pinned connection is refused because the server presented a
    /// certificate that is not from the pinned CA or does not name the host. Someone may be intercepting the
    /// connection (a TLS-inspection proxy, say), or the server's certificate was replaced.
    /// </summary>
    public event Action<RejectedCertificate>? CertificateRejected;

    public static string ComputeFingerprint(X509Certificate2 certificate) => HospitalCaPins.ComputeFingerprint(certificate);

    /// <summary>"AB12CD34…" → "AB12 CD34 …" for reading aloud / comparing on screen.</summary>
    public static string Format(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));

    /// <summary>
    /// Whether <paramref name="serverCertificate"/> may be trusted for <paramref name="host"/>: it names the host,
    /// is for a TLS server, and chains to exactly a pinned CA, directly or through <paramref name="intermediates"/>
    /// (the CA certificates the server sent in the handshake).
    /// </summary>
    public bool IsTrusted(X509Certificate2 serverCertificate, string host, X509Certificate2Collection? intermediates = null) =>
        Pins.IsTrusted(serverCertificate, host, intermediates);

    /// <summary>The TLS callback's check (<see cref="HospitalCaPins.Validate"/>).</summary>
    public bool Validate(X509Certificate? certificate, X509Chain? handshake, string host) => Pins.Validate(certificate, handshake, host);

    /// <summary>
    /// <see cref="Validate"/>, raising <see cref="CertificateRejected"/> when a presented certificate is refused.
    /// </summary>
    internal bool Check(X509Certificate? certificate, X509Chain? handshake, string host)
    {
        if (Validate(certificate, handshake, host))
        {
            return true;
        }

        if (certificate is not null && CertificateRejected is { } rejected)
        {
            using var certificate2 = new X509Certificate2(certificate);
            rejected(RejectedCertificate.From(certificate2, host));
        }

        return false;
    }

    /// <summary>Every certificate of the handshake other than the server's own.</summary>
    internal static X509Certificate2Collection Intermediates(X509Chain? handshake, X509Certificate2 server) =>
        HospitalCaPins.Intermediates(handshake, server);

    /// <summary>HttpClient for server calls that only accepts certificates issued by a pinned CA.</summary>
    public HttpClient CreateHttpClient(Uri baseAddress, TimeSpan timeout)
    {
        var host = baseAddress.Host;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, _) => Check(certificate, chain, host),
            },
        };
        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = timeout };
    }

    public void Dispose() => Pins.Dispose();
}
