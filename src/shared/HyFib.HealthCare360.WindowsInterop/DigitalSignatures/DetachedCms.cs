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
}

/// <summary>
/// Whether the certificate chains to a root this computer trusts, at the signing time. Best effort, reported and never
/// required: an Indian DSC chains to the CCA India root, which a hospital server may not carry, and the server checks
/// offline (no revocation lists or certificate downloads on the hospital LAN).
/// </summary>
public sealed record CertificateChainStatus(bool Trusted, IReadOnlyList<string> Problems)
{
    public string Summary => Trusted ? "Trusted" : Problems.Count == 0 ? "Not trusted" : string.Join("; ", Problems);
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
/// signer's certificate (end certificate only) and the signing time as a signed attribute.
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
    /// its PIN through the token's own dialog unless <paramref name="silent"/>.
    /// </summary>
    /// <returns>The DER-encoded SignedData, without the content.</returns>
    public static byte[] Sign(byte[] content, X509Certificate2 certificate, DateTimeOffset signingTime, bool silent = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(certificate);
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = Sha256,
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        cms.ComputeSignature(signer, silent);
        return cms.Encode();
    }

    /// <summary>
    /// Checks <paramref name="signature"/> over <paramref name="content"/>: one signer, a SHA-256 (or stronger) digest,
    /// a signature that matches the content, a signing time, and a certificate valid at that time whose key usage allows
    /// signing. The certificate chain is built as well, against this computer's trusted roots, and reported.
    /// </summary>
    public static CmsVerification Verify(byte[] content, byte[] signature)
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
        var signedAt = SigningTime(info);
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

        return new CmsVerification(true, null, "The signature is valid.", signer, at, Chain(certificate, cms.Certificates, at));
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

    // Offline on purpose: the server sits on the hospital LAN, and a slow CRL or AIA fetch must not hold up signing.
    private static CertificateChainStatus Chain(X509Certificate2 certificate, X509Certificate2Collection included, DateTimeOffset at)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = at.UtcDateTime;
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
