using System.Security.Cryptography;
using System.Text.Json;

namespace HyFib.HealthCare360.WindowsInterop.Updates;

/// <summary>Why a package was refused.</summary>
public enum ReleaseProblem
{
    /// <summary>There is no signature: the release was built without HyFib's signing key.</summary>
    Unsigned,

    /// <summary>The signature file is not one HC360 can read.</summary>
    UnreadableSignature,

    /// <summary>Signed with a key this version of HC360 does not trust (<see cref="ReleaseKeys"/>).</summary>
    UntrustedKey,

    /// <summary>The signature does not match the manifest: one of them was changed.</summary>
    BadSignature,

    /// <summary>The manifest is signed but says nothing HC360 can use.</summary>
    UnreadableManifest,

    /// <summary>The manifest is for another product.</summary>
    WrongProduct,

    /// <summary>The package is not the size the manifest says: it was changed, or did not download in full.</summary>
    WrongSize,

    /// <summary>The package's SHA-256 is not the manifest's: it was changed.</summary>
    WrongHash,

    /// <summary>The package is not newer than the version installed (or offered): HC360 never goes back a version by itself.</summary>
    NotNewer,

    /// <summary>The package needs a newer server than this one.</summary>
    NeedsNewerServer,
}

/// <summary>A package HC360 refused to install or offer, and why.</summary>
public sealed class ReleaseRejectedException : Exception
{
    public ReleaseRejectedException()
    {
    }

    public ReleaseRejectedException(string message)
        : base(message)
    {
    }

    public ReleaseRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ReleaseRejectedException(ReleaseProblem problem, string message)
        : base(message) => Problem = problem;

    public ReleaseProblem Problem { get; }
}

/// <summary>
/// The checks every copy of HC360 makes before it trusts a package it installs by itself (SRS DED-18): the Server Node
/// before it takes one in and before it offers it, HC360 Desktop before it downloads and after, and the updater, as
/// SYSTEM, before it installs. Each reads the files itself and trusts nothing another made of them.
/// <list type="number">
/// <item>The manifest's bytes are signed (ECDSA P-256, SHA-256) by a key in <see cref="ReleaseKeys"/>.</item>
/// <item>The manifest is for the expected product, and readable.</item>
/// <item>The package has the manifest's size and SHA-256.</item>
/// <item>It is newer than what is installed (no downgrades), and the server is new enough for it.</item>
/// </list>
/// </summary>
public static class ReleasePackages
{
    /// <summary>The product of HC360 Desktop's packages.</summary>
    public const string DesktopProduct = "HC360 Desktop";

    public const string ManifestSuffix = ".manifest.json";
    public const string SignatureSuffix = ".manifest.sig";

    /// <summary>A manifest is a few hundred bytes; anything much larger is not one.</summary>
    public const int MaxManifestBytes = 16 * 1024;

    public const int MaxSignatureBytes = 4 * 1024;

    /// <summary>The largest package HC360 takes (HC360 Desktop's is about 70 MB compressed).</summary>
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    /// <summary><c>HC360-Desktop-1.2.0-win-x64.zip</c> → <c>HC360-Desktop-1.2.0-win-x64.manifest.json</c>.</summary>
    public static string ManifestPathFor(string packagePath) => WithoutExtension(packagePath) + ManifestSuffix;

    /// <summary><c>HC360-Desktop-1.2.0-win-x64.zip</c> → <c>HC360-Desktop-1.2.0-win-x64.manifest.sig</c>.</summary>
    public static string SignaturePathFor(string packagePath) => WithoutExtension(packagePath) + SignatureSuffix;

    /// <summary>
    /// The manifest, once its signature verifies with a trusted key and it is a readable manifest for
    /// <paramref name="product"/>. An empty <paramref name="signature"/> means the package was not signed.
    /// </summary>
    /// <exception cref="ReleaseRejectedException">Any check failed; <see cref="ReleaseRejectedException.Problem"/> says which.</exception>
    public static ReleaseManifest ReadManifest(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature, IReadOnlyList<ReleaseKey> trusted, string product)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        CheckSignature(manifest, signature, trusted);

        ReleaseManifest? read;
        try
        {
            read = manifest.Length <= MaxManifestBytes ? JsonSerializer.Deserialize(WithoutBom(manifest), UpdatesJson.Default.ReleaseManifest) : null;
        }
        catch (JsonException)
        {
            read = null;
        }

        var problem = read is null ? "it is not a manifest" : Problem(read);
        if (read is null || problem is not null)
        {
            throw new ReleaseRejectedException(ReleaseProblem.UnreadableManifest, $"The signed manifest is not one HC360 can use: {problem}.");
        }

        if (!string.Equals(read.Product, product, StringComparison.Ordinal))
        {
            throw new ReleaseRejectedException(ReleaseProblem.WrongProduct, $"The package is {read.Product} {read.Version}, not {product}.");
        }

        return read;
    }

    /// <summary>Reads the package from its start to its end and checks it has the manifest's size and SHA-256.</summary>
    /// <exception cref="ReleaseRejectedException">It does not.</exception>
    public static void CheckPackage(Stream package, ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(manifest);
        package.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = package.Read(buffer, 0, buffer.Length)) > 0)
        {
            size += read;
            if (size > manifest.Size)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        Compare(size, hash.GetHashAndReset(), manifest);
    }

    /// <summary><see cref="CheckPackage"/>, reading asynchronously.</summary>
    public static async Task CheckPackageAsync(Stream package, ReleaseManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(manifest);
        package.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await package.ReadAsync(buffer, cancellationToken)) > 0)
        {
            size += read;
            if (size > manifest.Size)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        Compare(size, hash.GetHashAndReset(), manifest);
    }

    /// <summary>Refuses a package that is not newer than <paramref name="installedVersion"/> (no downgrades).</summary>
    /// <exception cref="ReleaseRejectedException">It is not newer.</exception>
    public static void CheckNewer(ReleaseManifest manifest, string? installedVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (ReleaseVersion.TryParse(installedVersion, out var installed) && manifest.ReleaseVersion <= installed)
        {
            throw new ReleaseRejectedException(ReleaseProblem.NotNewer, $"{manifest.Product} {manifest.Version} is not newer than {installed}, which is installed.");
        }
    }

    /// <summary>Refuses a package that needs a newer server than <paramref name="serverVersion"/>.</summary>
    /// <exception cref="ReleaseRejectedException">It needs a newer one.</exception>
    public static void CheckServer(ReleaseManifest manifest, string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.MinimumServerVersion is { } minimum && ReleaseVersion.TryParse(serverVersion, out var server)
            && ReleaseVersion.Parse(minimum) > server)
        {
            throw new ReleaseRejectedException(
                ReleaseProblem.NeedsNewerServer, $"{manifest.Product} {manifest.Version} needs HC360 server {minimum} or later; this server is {server}.");
        }
    }

    private static void CheckSignature(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature, IReadOnlyList<ReleaseKey> trusted)
    {
        if (signature.IsEmpty)
        {
            throw new ReleaseRejectedException(ReleaseProblem.Unsigned, "The package is not signed: HC360 installs only packages signed by HyFib.");
        }

        ReleaseSignatureFile? file;
        byte[]? bytes = null;
        try
        {
            file = signature.Length <= MaxSignatureBytes ? JsonSerializer.Deserialize(WithoutBom(signature), UpdatesJson.Default.ReleaseSignatureFile) : null;
            if (file is { Signature.Length: > 0 and <= 256 })
            {
                bytes = Convert.FromBase64String(file.Signature);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            file = null;
        }

        if (file is null || bytes is null || !string.Equals(file.Algorithm, ReleaseSignatureFile.EcdsaP256Sha256, StringComparison.Ordinal)
            || file.KeyId is not { Length: 16 } || !file.KeyId.All(char.IsAsciiHexDigit))
        {
            throw new ReleaseRejectedException(ReleaseProblem.UnreadableSignature, "The package's signature file is not one HC360 can read.");
        }

        var key = trusted.FirstOrDefault(k => string.Equals(k.Id, file.KeyId, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            throw new ReleaseRejectedException(
                ReleaseProblem.UntrustedKey, $"The package is signed with key {file.KeyId.ToLowerInvariant()}, which this version of HC360 does not trust.");
        }

        if (manifest.Length > MaxManifestBytes || !key.Verifies(manifest, bytes))
        {
            throw new ReleaseRejectedException(ReleaseProblem.BadSignature, "The package's signature does not match its manifest: one of them was changed.");
        }
    }

    private static void Compare(long size, byte[] sha256, ReleaseManifest manifest)
    {
        if (size != manifest.Size)
        {
            throw new ReleaseRejectedException(
                ReleaseProblem.WrongSize, $"The package is {(size > manifest.Size ? "larger" : "smaller")} than its manifest says ({manifest.Size:N0} bytes).");
        }

        if (!CryptographicOperations.FixedTimeEquals(sha256, Convert.FromHexString(manifest.Sha256)))
        {
            throw new ReleaseRejectedException(ReleaseProblem.WrongHash, "The package's SHA-256 is not the one in its manifest: the package was changed.");
        }
    }

    private static string? Problem(ReleaseManifest manifest) =>
        manifest.Format != ReleaseManifest.CurrentFormat ? $"format {manifest.Format}, not {ReleaseManifest.CurrentFormat}"
        : string.IsNullOrWhiteSpace(manifest.Product) ? "no product"
        : !ReleaseVersion.TryParse(manifest.Version, out _) ? "no version"
        : string.IsNullOrWhiteSpace(manifest.File) || manifest.File.Length > 128 || Path.GetFileName(manifest.File) != manifest.File
          || manifest.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "no file name"
        : manifest.Size is <= 0 or > MaxPackageBytes ? "no size"
        : manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(char.IsAsciiHexDigit) ? "no SHA-256"
        : manifest.MinimumServerVersion is { } minimum && !ReleaseVersion.TryParse(minimum, out _) ? "an unreadable minimum server version"
        : null;

    private static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> json) => json.StartsWith("﻿"u8) ? json[3..] : json;

    private static string WithoutExtension(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
}
