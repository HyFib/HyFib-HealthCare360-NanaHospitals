using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// Certificate pinning to the hospital's private CA (SRS DED-04/SEC-06). The server's LAN certificate must chain
/// to exactly this CA and name the host being contacted; the public Windows trust store is not consulted, so a
/// device on the WiFi cannot impersonate the server with some other valid certificate.
/// </summary>
internal sealed class ServerTrust : IDisposable
{
    private readonly X509Certificate2 _ca;

    public ServerTrust(string caPem)
    {
        _ca = X509Certificate2.CreateFromPem(caPem);
        Fingerprint = ComputeFingerprint(_ca);
    }

    /// <summary>SHA-256 of the DER certificate as upper-case hex (same format the server advertises).</summary>
    public string Fingerprint { get; }

    public DateTime NotAfter => _ca.NotAfter;

    public string Subject => _ca.Subject;

    public static string ComputeFingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>"AB12CD34…" → "AB12 CD34 …" for reading aloud / comparing on screen.</summary>
    public static string Format(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));

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

        var root = chain.ChainElements[^1].Certificate;
        return root.RawData.AsSpan().SequenceEqual(_ca.RawData);
    }

    public bool Validate(object? sender, X509Certificate? certificate, SslPolicyErrors errors, string host)
    {
        if (certificate is null)
        {
            return false;
        }

        using var certificate2 = new X509Certificate2(certificate);
        return IsTrusted(certificate2, host);
    }

    /// <summary>HttpClient for server calls that only accepts certificates issued by the pinned CA.</summary>
    public HttpClient CreateHttpClient(Uri baseAddress, TimeSpan timeout)
    {
        var host = baseAddress.Host;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = (sender, certificate, _, errors) => Validate(sender, certificate, errors, host) },
        };
        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = timeout };
    }

    public void Dispose() => _ca.Dispose();
}
