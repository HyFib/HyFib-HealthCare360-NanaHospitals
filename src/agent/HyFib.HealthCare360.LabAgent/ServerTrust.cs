using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>
/// Pins the server to the hospital's private CA, with the same rules as HC360 Desktop (SRS DED-04, DED-05): the server's
/// certificate must chain to exactly this CA and name the host the agent contacts. The Windows trust store is not
/// consulted, so a device on the network cannot pose as the server with some other valid certificate. Every call the
/// agent makes, the relay's WebSocket included, goes through this check; none ever accepts any certificate.
/// </summary>
internal sealed class ServerTrust : IDisposable
{
    private readonly X509Certificate2 _ca;

    public ServerTrust(string caPem)
    {
        _ca = X509Certificate2.CreateFromPem(caPem);
        Fingerprint = ComputeFingerprint(_ca);
    }

    /// <summary>SHA-256 of the DER certificate as upper-case hex (the format the server installer prints).</summary>
    public string Fingerprint { get; }

    public static string ComputeFingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>"AB12 CD34 …", "ab12:cd34:…" or "AB12CD34…" → "AB12CD34…"; null when it is not a SHA-256 fingerprint.</summary>
    public static string? NormalizeFingerprint(string? value)
    {
        var hex = new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray()).ToUpperInvariant();
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigitUpper) ? hex : null;
    }

    public bool IsTrusted(X509Certificate2 serverCertificate, string host)
    {
        if (!serverCertificate.MatchesHostname(host, allowWildcards: false, allowCommonName: false))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        // A private CA on an isolated LAN publishes no revocation lists.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = DateTime.Now;
        if (!chain.Build(serverCertificate))
        {
            return false;
        }

        return chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(_ca.RawData);
    }

    public bool Validate(X509Certificate? certificate, string host)
    {
        if (certificate is null)
        {
            return false;
        }

        using var certificate2 = new X509Certificate2(certificate);
        return IsTrusted(certificate2, host);
    }

    /// <summary>HttpClient for the agent's calls: pinned, no proxy (the server is on the hospital LAN), bounded waits.</summary>
    public HttpClient CreateHttpClient(Uri baseAddress, TimeSpan timeout)
    {
        var host = baseAddress.Host;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => Validate(certificate, host) },
        };
        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = timeout };
    }

    /// <summary>The same pinning for the relay's WebSocket.</summary>
    public void ApplyTo(ClientWebSocketOptions options, string host)
    {
        options.Proxy = null;
        options.RemoteCertificateValidationCallback = (_, certificate, _, _) => Validate(certificate, host);
    }

    public void Dispose() => _ca.Dispose();
}
