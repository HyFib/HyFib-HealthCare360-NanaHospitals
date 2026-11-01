using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.WindowsInterop.Trust;

/// <summary>
/// The hospital CAs a device trusts the server through (SRS DED-04, DED-05): the one it pins and, once it followed a
/// rollover ('certificates roll-ca', docs/deployment/CA-ROLLOVER.md), the one that CA replaced, as a second pin until it
/// expires. The server's certificate must name the host the device contacts, be for a TLS server, and chain to exactly one
/// of them; the Windows trust store is never consulted, so a device on the network cannot pose as the server with some
/// other valid certificate. HC360 Desktop and the lab agent check the server with this one copy of the code, and the
/// server's tests run it over TLS against a real rollover.
/// </summary>
public sealed class HospitalCaPins : IDisposable
{
    /// <summary>The TLS server authentication extended key usage (id-kp-serverAuth).</summary>
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    // Replaced whole when the device follows a rollover, so a check on another thread sees the pins before or after.
    private volatile Pins _pins;

    /// <param name="caPem">The CA the device pins.</param>
    /// <param name="previousCaPem">
    /// The CA it replaced in a rollover the device followed, if any. One that cannot be read is left out: it is a second pin
    /// only, and the device still trusts the server through the first.
    /// </param>
    public HospitalCaPins(string caPem, string? previousCaPem = null)
    {
        var ca = X509Certificate2.CreateFromPem(caPem);
        _pins = new Pins(ca, ComputeFingerprint(ca), Readable(previousCaPem));
    }

    /// <summary>The CA the device pins.</summary>
    public X509Certificate2 Ca => _pins.Ca;

    /// <summary>The CA that <see cref="Ca"/> replaced in a rollover the device followed; null when it followed none.</summary>
    public X509Certificate2? PreviousCa => _pins.Previous;

    /// <summary>SHA-256 of the pinned CA as upper-case hex: as the server announces it and its installer prints it.</summary>
    public string Fingerprint => _pins.Fingerprint;

    public static string ComputeFingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>
    /// The CAs the device trusts the server through at <paramref name="now"/>, as its heartbeat reports them: the one it pins,
    /// then the one it replaced while that is still valid. An expired CA vouches for nothing.
    /// </summary>
    public IReadOnlyList<string> Fingerprints(DateTimeOffset now)
    {
        var pins = _pins;
        return pins.Previous is { } previous && now.UtcDateTime < previous.NotAfter.ToUniversalTime()
            ? [pins.Fingerprint, ComputeFingerprint(previous)]
            : [pins.Fingerprint];
    }

    /// <summary>
    /// Whether <paramref name="serverCertificate"/> may be trusted for <paramref name="host"/>: it names the host, is for a
    /// TLS server, and chains to exactly one of the pinned CAs, directly or only through <paramref name="intermediates"/> (the
    /// certificates the server sent in the handshake).
    /// </summary>
    public bool IsTrusted(X509Certificate2 serverCertificate, string host, X509Certificate2Collection? intermediates = null)
    {
        if (!serverCertificate.MatchesHostname(host, allowWildcards: false, allowCommonName: false))
        {
            return false;
        }

        var pins = _pins;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(pins.Ca);
        if (pins.Previous is { } previous)
        {
            chain.ChainPolicy.CustomTrustStore.Add(previous);
        }

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

        // Built from what the server sent and the pinned CAs only: Windows also takes certificates from this computer's own
        // stores to build a chain, and whether the server is trusted must not depend on what happens to be there.
        for (var link = 1; link < chain.ChainElements.Count - 1; link++)
        {
            var linked = chain.ChainElements[link].Certificate.RawData;
            if (intermediates is null || !intermediates.Any(sent => sent.RawData.AsSpan().SequenceEqual(linked)))
            {
                return false;
            }
        }

        var root = chain.ChainElements[^1].Certificate.RawData.AsSpan();
        return root.SequenceEqual(pins.Ca.RawData) || (pins.Previous is { } replaced && root.SequenceEqual(replaced.RawData));
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
    public static X509Certificate2Collection Intermediates(X509Chain? handshake, X509Certificate2 server)
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

    /// <summary>
    /// Copies of what the server sent besides its own certificate, for <see cref="SuccessorIn"/> after the TLS callback: the
    /// platform disposes the handshake's certificates once the callback returns.
    /// </summary>
    public static X509Certificate2[] KeepSent(X509Certificate? certificate, X509Chain? handshake)
    {
        if (certificate is null)
        {
            return [];
        }

        using var server = new X509Certificate2(certificate);
        return [.. Intermediates(handshake, server).Select(sent => X509CertificateLoader.LoadCertificate(sent.RawData))];
    }

    /// <summary>
    /// After the server's CA was rolled over, its new CA, on the word of the CA pinned here: <paramref name="serverCaPem"/> is
    /// the server's CA certificate and <paramref name="sent"/> what the server sent, both over a connection pinned here
    /// (<see cref="HospitalCaSuccession"/>). Null when the server's CA is the pinned one, or the pinned CA did not vouch for it.
    /// </summary>
    public HospitalCaSuccessor? SuccessorIn(string serverCaPem, IEnumerable<X509Certificate2> sent, DateTimeOffset now)
    {
        var pinned = _pins.Ca;
        using var candidate = X509Certificate2.CreateFromPem(serverCaPem);
        if (candidate.RawData.AsSpan().SequenceEqual(pinned.RawData)
            || HospitalCaSuccession.CrossFor(pinned, candidate, sent, now) is not { } cross)
        {
            return null;
        }

        return new HospitalCaSuccessor(candidate.ExportCertificatePem(), ComputeFingerprint(candidate), cross.ExportCertificatePem());
    }

    /// <summary>Pins <paramref name="successor"/>, keeping the CA it replaced as the second pin until that expires.</summary>
    public void Follow(HospitalCaSuccessor successor)
    {
        ArgumentNullException.ThrowIfNull(successor);
        var ca = X509Certificate2.CreateFromPem(successor.CaPem);
        // The pin before the previous one is left to the collector: a check on another thread may still hold it.
        _pins = new Pins(ca, ComputeFingerprint(ca), _pins.Ca);
    }

    public void Dispose()
    {
        var pins = _pins;
        pins.Ca.Dispose();
        pins.Previous?.Dispose();
    }

    private static X509Certificate2? Readable(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private sealed record Pins(X509Certificate2 Ca, string Fingerprint, X509Certificate2? Previous);
}

/// <summary>
/// A hospital CA a device follows after a rollover: its certificate, its fingerprint, and the certificate the CA it replaced
/// signed for its key, which the device keeps to show that it succeeded the CA it pinned (an IT policy may name that one).
/// </summary>
public sealed record HospitalCaSuccessor(string CaPem, string Fingerprint, string CrossPem);
