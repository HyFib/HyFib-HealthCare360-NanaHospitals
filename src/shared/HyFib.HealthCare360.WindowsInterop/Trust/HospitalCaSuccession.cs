using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.WindowsInterop.Trust;

/// <summary>
/// Whether a hospital CA a device does not pin yet succeeds the one it pins, as the server's CA is rolled over
/// ('certificates prepare-ca', then 'switch-ca'; docs/deployment/CA-ROLLOVER.md). A device holds a new CA only on the word of
/// the CA it pins: the server hands out, with the next CA, the certificate the current CA signed for the next CA's key (the
/// cross certificate). HC360 Desktop and the lab agent decide with this one copy of the code, and the server's tests check it
/// against a real rollover.
/// </summary>
public static class HospitalCaSuccession
{
    /// <summary>
    /// True when <paramref name="candidate"/> is a certificate authority in <paramref name="pinned"/>'s name, with another key,
    /// and <paramref name="handedOut"/> (what the server handed out with it, over a connection pinned to
    /// <paramref name="pinned"/>) holds a certificate authority for the candidate's name and key that <paramref name="pinned"/>
    /// signed, valid at <paramref name="now"/>. The candidate's own signature on itself is not what is trusted: the pinned
    /// CA's signature on its key is.
    /// </summary>
    public static bool Succeeds(X509Certificate2 pinned, X509Certificate2 candidate, IEnumerable<X509Certificate2> handedOut, DateTimeOffset now) =>
        CrossFor(pinned, candidate, handedOut, now) is not null;

    /// <summary>
    /// The certificate in <paramref name="handedOut"/> that makes <paramref name="candidate"/> the successor of
    /// <paramref name="pinned"/> (see <see cref="Succeeds"/>), or null when there is none.
    /// </summary>
    public static X509Certificate2? CrossFor(X509Certificate2 pinned, X509Certificate2 candidate, IEnumerable<X509Certificate2> handedOut, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(handedOut);
        if (!SameName(candidate, pinned) || !SelfIssued(candidate) || SameKey(candidate, pinned) || !IsCa(candidate))
        {
            return null;
        }

        return handedOut.FirstOrDefault(cross =>
            !cross.RawData.AsSpan().SequenceEqual(candidate.RawData)
            && SameName(cross, candidate)
            && SameKey(cross, candidate)
            && IsCa(cross)
            && cross.NotBefore.ToUniversalTime() <= now.UtcDateTime && now.UtcDateTime < cross.NotAfter.ToUniversalTime()
            && SignedBy(cross, pinned, now));
    }

    /// <summary>
    /// Whether <paramref name="cross"/>, kept since a device took <paramref name="current"/> as the next CA, shows that it
    /// succeeds <paramref name="previous"/>. It is checked as of when the previous CA signed it: that the previous CA vouched
    /// for the current one stays true after the cross certificate expires.
    /// </summary>
    public static bool Succeeded(X509Certificate2 previous, X509Certificate2 current, X509Certificate2 cross)
    {
        ArgumentNullException.ThrowIfNull(cross);
        return Succeeds(previous, current, [cross], new DateTimeOffset(cross.NotBefore.ToUniversalTime()));
    }

    private static bool SameName(X509Certificate2 a, X509Certificate2 b) => a.SubjectName.RawData.AsSpan().SequenceEqual(b.SubjectName.RawData);

    private static bool SelfIssued(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    private static bool SameKey(X509Certificate2 a, X509Certificate2 b) =>
        a.PublicKey.EncodedKeyValue.RawData.AsSpan().SequenceEqual(b.PublicKey.EncodedKeyValue.RawData);

    private static bool IsCa(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true };

    /// <summary>Whether <paramref name="issuer"/>, and no one else, signed <paramref name="certificate"/>: the one link from it to the issuer.</summary>
    private static bool SignedBy(X509Certificate2 certificate, X509Certificate2 issuer, DateTimeOffset now)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(issuer);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        return chain.Build(certificate)
            && chain.ChainElements.Count == 2
            && chain.ChainElements[0].Certificate.RawData.AsSpan().SequenceEqual(certificate.RawData)
            && chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(issuer.RawData);
    }
}
