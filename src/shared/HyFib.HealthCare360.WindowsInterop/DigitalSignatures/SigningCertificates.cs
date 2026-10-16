using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

/// <summary>What HC360 shows and keeps of the certificate that signed a document.</summary>
/// <param name="Subject">The subject's distinguished name.</param>
/// <param name="SignerName">The subject's common name (the doctor's name on an Indian DSC), else the whole subject.</param>
/// <param name="SerialNumber">The serial number in hex, as the issuer and Windows show it.</param>
/// <param name="Issuer">The issuing CA's distinguished name.</param>
/// <param name="Thumbprint">The SHA-1 thumbprint in hex, as the Windows certificate store shows it.</param>
public sealed record SignerCertificate(
    string Subject,
    string SignerName,
    string SerialNumber,
    string Issuer,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter)
{
    public static SignerCertificate Of(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return new SignerCertificate(
            certificate.Subject,
            string.IsNullOrWhiteSpace(name) ? certificate.Subject : name,
            certificate.SerialNumber,
            certificate.Issuer,
            certificate.Thumbprint,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero));
    }
}

/// <summary>
/// Which certificates may sign a prescription (ERX-04). Under the IT Act 2000 and the CCA's rules a doctor signs with
/// their own Digital Signature Certificate; a DSC on a USB token is registered in the Windows "My" store by the token
/// vendor's CSP/KSP, so HC360 Desktop lists that store and keeps only the certificates that can sign documents now.
/// </summary>
public static class SigningCertificates
{
    /// <summary>Why a certificate cannot sign; null when it can.</summary>
    /// <remarks>
    /// The certificate must have a private key (on the token or the computer), be valid at <paramref name="now"/>, and
    /// carry a key usage extension that allows digital signatures or non-repudiation (content commitment). A
    /// certificate without key usage is refused too: DSCs always state it, and a certificate that does not is not one.
    /// </remarks>
    public static string? Problem(X509Certificate2 certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            return "The certificate has no private key on this computer or token.";
        }

        if (!IsTimeValid(certificate, now))
        {
            return "The certificate is not valid now.";
        }

        return AllowsSigning(certificate) ? null : "The certificate is not for signing documents (key usage).";
    }

    public static bool CanSign(X509Certificate2 certificate, DateTimeOffset now) => Problem(certificate, now) is null;

    /// <summary>Valid at <paramref name="at"/>: on or after NotBefore and on or before NotAfter.</summary>
    public static bool IsTimeValid(X509Certificate2 certificate, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var utc = at.UtcDateTime;
        return utc >= certificate.NotBefore.ToUniversalTime() && utc <= certificate.NotAfter.ToUniversalTime();
    }

    /// <summary>The key usage extension allows digital signatures or non-repudiation.</summary>
    public static bool AllowsSigning(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is not null
            && (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) != 0;
    }

    /// <summary>The certificates of <paramref name="candidates"/> that can sign now, the latest to expire first.</summary>
    public static IReadOnlyList<X509Certificate2> Usable(IEnumerable<X509Certificate2> candidates, DateTimeOffset now) =>
        [.. candidates.Where(c => CanSign(c, now)).OrderByDescending(c => c.NotAfter)];
}
