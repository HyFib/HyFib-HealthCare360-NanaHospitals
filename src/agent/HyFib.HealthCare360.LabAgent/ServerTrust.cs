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
    /// <summary>The TLS server authentication extended key usage (id-kp-serverAuth).</summary>
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

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

    /// <summary>
    /// Whether <paramref name="serverCertificate"/> may be trusted for <paramref name="host"/>: it names the host,
    /// is for a TLS server, and chains to exactly the pinned CA, directly or only through <paramref name="intermediates"/>
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

        // Built from what the server sent and the pinned CA only: Windows also takes certificates from this computer's own
        // stores to build a chain, and whether the server is trusted must not depend on what happens to be there.
        for (var link = 1; link < chain.ChainElements.Count - 1; link++)
        {
            var linked = chain.ChainElements[link].Certificate.RawData;
            if (intermediates is null || !intermediates.Any(sent => sent.RawData.AsSpan().SequenceEqual(linked)))
            {
                return false;
            }
        }

        return chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(_ca.RawData);
    }

    /// <summary>
    /// The TLS callback: <paramref name="handshake"/> is the chain .NET built for the server's certificate, whose extra store
    /// holds what the server sent. Only that is offered: the chain's own elements may come from this computer's stores.
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

    /// <summary>What the server sent in the handshake besides its own certificate.</summary>
    internal static X509Certificate2Collection Intermediates(X509Chain? handshake, X509Certificate2 server)
    {
        var intermediates = new X509Certificate2Collection();
        if (handshake is null)
        {
            return intermediates;
        }

        foreach (var sent in handshake.ChainPolicy.ExtraStore)
        {
            if (!sent.RawData.AsSpan().SequenceEqual(server.RawData))
            {
                intermediates.Add(sent);
            }
        }

        return intermediates;
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

    public void Dispose() => _ca.Dispose();
}
