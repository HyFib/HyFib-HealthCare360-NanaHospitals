using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

/// <summary>
/// The Certifying Authorities a doctor's DSC must chain to (ERX-04). Under the IT Act 2000 a DSC is issued by a CA that
/// the Controller of Certifying Authorities (CCA) has licensed, and every licensed CA's certificate is issued under a
/// CCA India root. HC360 trusts only the certificates IT puts in its DSC trust folder — the CCA India roots, and if
/// signatures arrive without them, the licensed CAs' certificates — and never the operating system's roots: those hold
/// every public web CA and, on a domain, the hospital's own enterprise CA, none of which issues DSCs.
/// </summary>
public sealed class DscTrustAnchors
{
    /// <summary>The files read from the folder: DER or PEM, a PEM file may hold several certificates.</summary>
    public static readonly IReadOnlyList<string> FileExtensions = [".cer", ".crt", ".der", ".pem"];

    private DscTrustAnchors(
        X509Certificate2Collection roots,
        X509Certificate2Collection intermediates,
        IReadOnlyList<string> problems,
        string? folderProblem = null)
    {
        Roots = roots;
        Intermediates = intermediates;
        Problems = problems;
        FolderProblem = folderProblem;
    }

    /// <summary>No folder, or an empty one: no DSC can be trusted.</summary>
    public static DscTrustAnchors None { get; } = new([], [], []);

    /// <summary>The self-issued certificates: the roots a chain must end at.</summary>
    public X509Certificate2Collection Roots { get; }

    /// <summary>The other certificates: CAs that help build a chain, never trusted on their own.</summary>
    public X509Certificate2Collection Intermediates { get; }

    /// <summary>Files in the folder that could not be read as certificates, for the person setting it up.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Why the folder itself could not be read (its permissions, a disk error), or null when it could: such a folder
    /// trusts nothing, as an empty one does.
    /// </summary>
    public string? FolderProblem { get; }

    public bool IsEmpty => Roots.Count == 0;

    /// <summary>A folder that could not be read: no DSC can be trusted, and <see cref="FolderProblem"/> says why.</summary>
    public static DscTrustAnchors Unreadable(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        return new DscTrustAnchors([], [], [], problem);
    }

    public static DscTrustAnchors Of(IEnumerable<X509Certificate2> certificates, IReadOnlyList<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        var roots = new X509Certificate2Collection();
        var intermediates = new X509Certificate2Collection();
        foreach (var certificate in certificates)
        {
            (IsSelfIssued(certificate) ? roots : intermediates).Add(certificate);
        }

        return new DscTrustAnchors(roots, intermediates, problems ?? []);
    }

    /// <summary>
    /// Reads every certificate file in <paramref name="directory"/>. A missing folder trusts nothing, and so does one that
    /// cannot be listed (<see cref="Unreadable"/>): the caller's checks then find no root, rather than failing.
    /// </summary>
    public static DscTrustAnchors Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        IReadOnlyList<string> files;
        try
        {
            files = Files(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed while it was being listed: a missing folder.
            return Directory.Exists(directory) ? Unreadable(ex.Message) : None;
        }

        return Read(files);
    }

    /// <summary>
    /// Reads the certificate files <paramref name="paths"/>; one that is not a certificate, or cannot be read, is named in
    /// <see cref="Problems"/>.
    /// </summary>
    public static DscTrustAnchors Read(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var certificates = new List<X509Certificate2>();
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                certificates.AddRange(Read(path));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return Of(certificates, problems);
    }

    /// <summary>The certificate files in <paramref name="directory"/>, by name; none when it does not exist.</summary>
    /// <exception cref="IOException">The folder cannot be listed (a disk error, or removed meanwhile).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder's permissions do not let this account list it.</exception>
    public static IReadOnlyList<string> Files(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory)
                .Where(p => FileExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Subject and issuer are the same name: a root (or a self-signed certificate).</summary>
    public static bool IsSelfIssued(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
    }

    private static X509Certificate2Collection Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var collection = new X509Certificate2Collection();
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            collection.ImportFromPem(text);
            if (collection.Count == 0)
            {
                throw new CryptographicException("The file holds no PEM certificate.");
            }
        }
        else
        {
            collection.Add(X509CertificateLoader.LoadCertificate(bytes));
        }

        return collection;
    }
}
