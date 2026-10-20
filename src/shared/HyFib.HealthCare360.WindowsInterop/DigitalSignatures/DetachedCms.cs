using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

/// <summary>Why a signature was refused.</summary>
public enum CmsProblem
{
    /// <summary>Not a DER-encoded CMS SignedData.</summary>
    Unreadable,

    /// <summary>Not exactly one signer.</summary>
    NotOneSigner,

    /// <summary>The signer's certificate is not in the signature.</summary>
    NoCertificate,

    /// <summary>The signature does not match the content: the content or the signature was changed.</summary>
    BadSignature,

    /// <summary>The digest is not SHA-256 or stronger.</summary>
    WeakDigest,

    /// <summary>The signed attributes carry no signing time.</summary>
    NoSigningTime,

    /// <summary>The certificate was not valid when the content was signed.</summary>
    CertificateNotValidAtSigningTime,

    /// <summary>The certificate's key usage does not allow signing documents.</summary>
    NotForSigning,

    /// <summary>The certificate is a Certifying Authority's, not a person's.</summary>
    CertificateAuthority,
}

/// <summary>
/// Whether the certificate chains, at the signing time, to one of the DSC roots IT installed for HC360
/// (<see cref="DscTrustAnchors"/>: the CCA India roots), and if not, why. Checked offline: no revocation lists or
/// certificate downloads on the hospital LAN, so a revoked DSC is not detected.
/// </summary>
public sealed record CertificateChainStatus(bool Trusted, IReadOnlyList<string> Problems)
{
    public const string TrustedSummary = "Trusted";

    public const string NoTrustAnchors = "No CCA India root certificates are installed for HC360 to check DSCs against";

    public string Summary => Trusted ? TrustedSummary : Problems.Count == 0 ? "Not trusted" : string.Join("; ", Problems);
}

/// <summary>The result of checking a detached signature against its content.</summary>
public sealed record CmsVerification(
    bool IsValid,
    CmsProblem? Problem,
    string Message,
    SignerCertificate? Signer,
    DateTimeOffset? SignedAt,
    CertificateChainStatus? Chain);

/// <summary>
/// Detached CMS/PKCS#7 SignedData (RFC 5652) with SHA-256 over a document's canonical bytes (ERX-04): HC360 Desktop
/// signs with the doctor's DSC, the server verifies, and anyone can check the same file with
/// <c>openssl cms -verify -binary -inform DER -in x.p7s -content canonical.json -noverify</c>. The signature carries the
/// signer's certificate, the CA certificates above it that the signing computer has (not the root), and the signing
/// time as a signed attribute.
/// </summary>
public static class DetachedCms
{
    private static readonly Oid Sha256 = new("2.16.840.1.101.3.4.2.1");

    private static readonly HashSet<string> StrongDigests = new(StringComparer.Ordinal)
    {
        "2.16.840.1.101.3.4.2.1", // SHA-256
        "2.16.840.1.101.3.4.2.2", // SHA-384
        "2.16.840.1.101.3.4.2.3", // SHA-512
    };

    /// <summary>
    /// Signs <paramref name="content"/> with <paramref name="certificate"/>'s private key. A key on a USB token asks for
    /// its PIN through the token's own dialog unless <paramref name="silent"/>. <paramref name="issuers"/> (the CA
    /// certificates between the certificate and its root, <see cref="SigningCertificates.Issuers"/>) go into the
    /// signature too, so the server can build the chain to the CCA India root without downloading them.
    /// </summary>
    /// <returns>The DER-encoded SignedData, without the content.</returns>
    public static byte[] Sign(
        byte[] content,
        X509Certificate2 certificate,
        DateTimeOffset signingTime,
        bool silent = false,
        IEnumerable<X509Certificate2>? issuers = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(certificate);
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = Sha256,
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        foreach (var issuer in issuers ?? [])
        {
            signer.Certificates.Add(issuer);
        }

        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        cms.ComputeSignature(signer, silent);
        return cms.Encode();
    }

    /// <summary>
    /// Checks <paramref name="signature"/> over <paramref name="content"/>: one signer, a SHA-256 (or stronger) digest,
    /// a signature that matches the content, a signing time, and a person's certificate (not a CA's) valid at that time
    /// whose key usage allows signing. The certificate chain is built as well, to <paramref name="trust"/>'s roots only,
    /// and reported in <see cref="CmsVerification.Chain"/>: whether that is required is the caller's decision. Whatever
    /// the bytes, a signature that does not check out is refused with a <see cref="CmsProblem"/>, never an exception: the
    /// parts .NET decodes only when read (the signing time, the certificate's extensions) are read with that in mind.
    /// </summary>
    public static CmsVerification Verify(byte[] content, byte[] signature, DscTrustAnchors? trust = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(signature);
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        try
        {
            cms.Decode(signature);
        }
        catch (CryptographicException)
        {
            return Refused(CmsProblem.Unreadable, "The signature is not a CMS/PKCS#7 signature.");
        }

        if (cms.SignerInfos.Count != 1)
        {
            return Refused(CmsProblem.NotOneSigner, "The signature must have exactly one signer.");
        }

        var info = cms.SignerInfos[0];
        if (info.Certificate is not { } certificate)
        {
            return Refused(CmsProblem.NoCertificate, "The signature does not include the signer's certificate.");
        }

        var signer = SignerCertificate.Of(certificate);
        DateTimeOffset? signedAt;
        try
        {
            signedAt = SigningTime(info);
        }
        catch (CryptographicException)
        {
            // Pkcs9SigningTime decodes its value only when it is read: one that is not a time is refused as no time at all,
            // not left to fail the request.
            return Refused(CmsProblem.NoSigningTime, "The signature's signing time cannot be read.", signer);
        }

        if (!StrongDigests.Contains(info.DigestAlgorithm.Value ?? string.Empty))
        {
            return Refused(CmsProblem.WeakDigest, "The signature must use SHA-256 or stronger.", signer, signedAt);
        }

        try
        {
            info.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            return Refused(CmsProblem.BadSignature, "The signature does not match the prescription: one of them was changed.", signer, signedAt);
        }

        if (signedAt is not { } at)
        {
            return Refused(CmsProblem.NoSigningTime, "The signature does not say when it was made.", signer, null);
        }

        if (!SigningCertificates.IsTimeValid(certificate, at))
        {
            return Refused(CmsProblem.CertificateNotValidAtSigningTime, "The certificate was not valid when the prescription was signed.", signer, at);
        }

        if (!SigningCertificates.AllowsSigning(certificate))
        {
            return Refused(CmsProblem.NotForSigning, "The certificate is not for signing documents.", signer, at);
        }

        if (!SigningCertificates.IsEndEntity(certificate))
        {
            return Refused(CmsProblem.CertificateAuthority, "The certificate belongs to a Certifying Authority, not to a person.", signer, at);
        }

        return new CmsVerification(true, null, "The signature is valid.", signer, at, Chain(certificate, cms.Certificates, at, trust ?? DscTrustAnchors.None));
    }

    private static CmsVerification Refused(CmsProblem problem, string message, SignerCertificate? signer = null, DateTimeOffset? signedAt = null) =>
        new(false, problem, message, signer, signedAt, null);

    private static DateTimeOffset? SigningTime(SignerInfo info)
    {
        foreach (var attribute in info.SignedAttributes)
        {
            foreach (var value in attribute.Values)
            {
                if (value is Pkcs9SigningTime time)
                {
                    return new DateTimeOffset(time.SigningTime.ToUniversalTime(), TimeSpan.Zero);
                }
            }
        }

        return null;
    }

    // To the DSC roots only (CustomRootTrust): the certificates the signature carries help build the chain but are never
    // trusted themselves. Offline on purpose: the server sits on the hospital LAN, and a slow CRL or AIA fetch must not
    // hold up signing.
    private static CertificateChainStatus Chain(X509Certificate2 certificate, X509Certificate2Collection included, DateTimeOffset at, DscTrustAnchors trust)
    {
        if (trust.IsEmpty)
        {
            return new CertificateChainStatus(false, [CertificateChainStatus.NoTrustAnchors]);
        }

        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(trust.Roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = at.UtcDateTime;
            chain.ChainPolicy.ExtraStore.AddRange(trust.Intermediates);
            chain.ChainPolicy.ExtraStore.AddRange(included);
            var trusted = chain.Build(certificate);
            var problems = chain.ChainStatus.Select(s => string.IsNullOrWhiteSpace(s.StatusInformation) ? s.Status.ToString() : s.StatusInformation.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new CertificateChainStatus(trusted, trusted ? [] : problems);
        }
        catch (CryptographicException ex)
        {
            return new CertificateChainStatus(false, [ex.Message]);
        }
    }
}
