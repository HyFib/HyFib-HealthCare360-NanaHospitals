using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
/// Certificate pinning to the hospital's private CA (SRS DED-04/SEC-06). The server's LAN certificate must chain
/// to exactly this CA and name the host being contacted; the public Windows trust store is not consulted, so a
/// device on the WiFi cannot impersonate the server with some other valid certificate.
/// </summary>
internal sealed class ServerTrust : IDisposable
{
    /// <summary>The TLS server authentication extended key usage (id-kp-serverAuth).</summary>
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

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

    /// <summary>
    /// Raised, on the connection's thread, when a pinned connection is refused because the server presented a
    /// certificate that is not from the pinned CA or does not name the host. Someone may be intercepting the
    /// connection (a TLS-inspection proxy, say), or the server's certificate was replaced.
    /// </summary>
    public event Action<RejectedCertificate>? CertificateRejected;

    public static string ComputeFingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>"AB12CD34…" → "AB12 CD34 …" for reading aloud / comparing on screen.</summary>
    public static string Format(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));

    /// <summary>
    /// Whether <paramref name="serverCertificate"/> may be trusted for <paramref name="host"/>: it names the host,
    /// is for a TLS server, and chains to exactly the pinned CA, directly or through <paramref name="intermediates"/>
    /// (the CA certificates the server sent in the handshake).
    /// </summary>
    public bool IsTrusted(
        X509Certificate2 serverCertificate, string host, X509Certificate2Collection? intermediates = null)
    {
        if (!serverCertificate.MatchesHostname(host, allowWildcards: false, allowCommonName: false))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        if (intermediates is not null)
        {
            chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        }

        chain.ChainPolicy.ApplicationPolicy.Add(ServerAuthentication);
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

    /// <summary>
    /// The TLS callback: <paramref name="handshake"/> is the chain .NET built for the server's certificate. The
    /// intermediates the server sent are in its extra store, and those .NET could place are in its elements too; both
    /// are offered, so a certificate issued below the pinned CA is trusted whichever way the platform passes them.
    /// </summary>
    public bool Validate(X509Certificate? certificate, X509Chain? handshake, string host)
    {
        if (certificate is null)
        {
            return false;
        }

        using var certificate2 = new X509Certificate2(certificate);
        return IsTrusted(certificate2, host, Intermediates(handshake, certificate2));
    }

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
    internal static X509Certificate2Collection Intermediates(X509Chain? handshake, X509Certificate2 server)
    {
        var intermediates = new X509Certificate2Collection();
        if (handshake is null)
        {
            return intermediates;
        }

        intermediates.AddRange(handshake.ChainPolicy.ExtraStore);
        foreach (var element in handshake.ChainElements)
        {
            if (!element.Certificate.RawData.AsSpan().SequenceEqual(server.RawData))
            {
                intermediates.Add(element.Certificate);
            }
        }

        return intermediates;
    }

    /// <summary>HttpClient for server calls that only accepts certificates issued by the pinned CA.</summary>
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

    public void Dispose() => _ca.Dispose();
}
