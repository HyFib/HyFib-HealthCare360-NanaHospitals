using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.WindowsInterop.Trust;

/// <summary>
/// The hospital CAs a device trusts the server through (SRS DED-04, DED-05), as the hospital CA is rolled over in two steps
/// (docs/deployment/CA-ROLLOVER.md):
/// <list type="bullet">
/// <item>the CA it pins (<see cref="Ca"/>), which issues the server's certificate;</item>
/// <item>after 'certificates prepare-ca', the next CA (<see cref="NextCa"/>), which the device holds on the pinned CA's word,
/// so that it still trusts the server after 'certificates switch-ca';</item>
/// <item>after the switch, the CA the pinned one replaced (<see cref="PreviousCa"/>), until it expires, so that a standby
/// server not switched yet still serves the device.</item>
/// </list>
/// The server's certificate must name the host the device contacts, be for a TLS server, and chain to exactly one of them;
/// the Windows trust store is never consulted, so a device on the network cannot pose as the server with some other valid
/// certificate. HC360 Desktop and the lab agent check the server with this one copy of the code, and the server's tests run
/// it over TLS against a real rollover.
/// </summary>
public sealed class HospitalCaPins : IDisposable
{
    /// <summary>The TLS server authentication extended key usage (id-kp-serverAuth).</summary>
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    // Replaced whole when the device holds the next CA or switches to it, so a check on another thread sees the pins before
    // or after a change, never half of one.
    private volatile Pins _pins;

    /// <param name="caPem">The CA the device pins.</param>
    /// <param name="previousCaPem">The CA <paramref name="caPem"/> replaced in a switch the device followed, if any.</param>
    /// <param name="nextCaPem">The next CA the server prepared, which the device holds, if any.</param>
    /// <remarks>
    /// A second pin that cannot be read is left out: the device still trusts the server through the first, and takes the next
    /// CA from the server again.
    /// </remarks>
    public HospitalCaPins(string caPem, string? previousCaPem = null, string? nextCaPem = null)
    {
        var ca = X509Certificate2.CreateFromPem(caPem);
        _pins = new Pins(ca, ComputeFingerprint(ca), Readable(previousCaPem), Readable(nextCaPem));
    }

    /// <summary>The CA the device pins.</summary>
    public X509Certificate2 Ca => _pins.Ca;

    /// <summary>The CA that <see cref="Ca"/> replaced in a switch the device followed; null when it followed none.</summary>
    public X509Certificate2? PreviousCa => _pins.Previous;

    /// <summary>The next CA the server prepared, which the device holds until the server switches to it; null when none.</summary>
    public X509Certificate2? NextCa => _pins.Next;

    /// <summary>SHA-256 of the pinned CA as upper-case hex: as the server announces it and its installer prints it.</summary>
    public string Fingerprint => _pins.Fingerprint;

    public static string ComputeFingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>
    /// The CAs the device trusts the server through at <paramref name="now"/>, as its heartbeat reports them: the one it pins,
    /// then the next one it holds, then the one the pinned CA replaced, each second pin while it is valid. An expired CA
    /// vouches for nothing.
    /// </summary>
    public IReadOnlyList<string> Fingerprints(DateTimeOffset now)
    {
        var pins = _pins;
        return [pins.Fingerprint, .. pins.Seconds.Where(ca => ValidAt(ca, now)).Select(ComputeFingerprint)];
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
        foreach (var pinned in pins.All)
        {
            chain.ChainPolicy.CustomTrustStore.Add(pinned);
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

        var root = chain.ChainElements[^1].Certificate.RawData;
        return pins.All.Any(pinned => pinned.RawData.AsSpan().SequenceEqual(root));
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
    /// The next CA the server prepared, on the word of the CA pinned here: <paramref name="handedOut"/> is what the server
    /// hands out at <see cref="HospitalCaFollow.NextCaPath"/> over a connection pinned here, the next CA and the certificate
    /// the pinned CA signed for its key (<see cref="HospitalCaSuccession"/>). Null when the pinned CA does not vouch for it,
    /// or the device holds it already.
    /// </summary>
    public HospitalCaNext? NextIn(string handedOut, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(handedOut);
        var pins = _pins;
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(handedOut);
            foreach (var candidate in certificates)
            {
                if (pins.All.Any(pinned => pinned.RawData.AsSpan().SequenceEqual(candidate.RawData)))
                {
                    continue;
                }

                if (HospitalCaSuccession.CrossFor(pins.Ca, candidate, certificates, now) is { } cross)
                {
                    return new HospitalCaNext(candidate.ExportCertificatePem(), ComputeFingerprint(candidate), cross.ExportCertificatePem());
                }
            }

            return null;
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>Whether <paramref name="serverCaPem"/>, the server's CA, is the next CA the device holds: the server switched.</summary>
    public bool IsNext(string serverCaPem)
    {
        if (_pins.Next is not { } next)
        {
            return false;
        }

        using var serverCa = X509Certificate2.CreateFromPem(serverCaPem);
        return serverCa.RawData.AsSpan().SequenceEqual(next.RawData);
    }

    /// <summary>Holds <paramref name="next"/>, the next CA the server prepared, as a second pin.</summary>
    public void Hold(HospitalCaNext next)
    {
        ArgumentNullException.ThrowIfNull(next);
        // A next CA held before is left to the collector: a check on another thread may still hold it.
        _pins = _pins with { Next = X509Certificate2.CreateFromPem(next.CaPem) };
    }

    /// <summary>
    /// Pins the next CA held, the server's CA since the switch, and keeps the CA it replaced as the second pin until that
    /// expires. Does nothing when no next CA is held.
    /// </summary>
    public void SwitchToNext()
    {
        var pins = _pins;
        if (pins.Next is not { } next)
        {
            return;
        }

        // The pin the previous one replaced is left to the collector: a check on another thread may still hold it.
        _pins = new Pins(next, ComputeFingerprint(next), pins.Ca, Next: null);
    }

    public void Dispose()
    {
        foreach (var pinned in _pins.All)
        {
            pinned.Dispose();
        }
    }

    private static bool ValidAt(X509Certificate2 certificate, DateTimeOffset now) =>
        certificate.NotBefore.ToUniversalTime() <= now.UtcDateTime && now.UtcDateTime < certificate.NotAfter.ToUniversalTime();

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

    private sealed record Pins(X509Certificate2 Ca, string Fingerprint, X509Certificate2? Previous, X509Certificate2? Next)
    {
        /// <summary>The second pins: the next CA, then the previous one.</summary>
        public IEnumerable<X509Certificate2> Seconds => new[] { Next, Previous }.OfType<X509Certificate2>();

        /// <summary>The pinned CA, then the second pins.</summary>
        public IEnumerable<X509Certificate2> All => Seconds.Prepend(Ca);
    }
}

/// <summary>
/// The next hospital CA a device holds while the server prepares a switch to it: its certificate, its fingerprint, and the
/// certificate the CA the device pins signed for its key, which the device keeps to show that the next CA succeeds the one
/// it pins (an IT policy may name either).
/// </summary>
public sealed record HospitalCaNext(string CaPem, string Fingerprint, string CrossPem);
