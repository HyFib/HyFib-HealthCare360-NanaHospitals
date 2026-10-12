using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.WindowsInterop.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Updates;

/// <summary>
/// The release signing keys the server checks HC360 Desktop packages against: the ones built in
/// (<see cref="ReleaseKeys.Trusted"/>), which nothing on the server can change. Tests register their own.
/// </summary>
public sealed class ReleaseTrust(IReadOnlyList<ReleaseKey> keys)
{
    public static ReleaseTrust BuiltIn { get; } = new(ReleaseKeys.Trusted);

    public IReadOnlyList<ReleaseKey> Keys => keys;
}

/// <summary>The HC360 Desktop package the server offers, verified: its manifest and signature as they are on disk.</summary>
public sealed record DesktopOffer(ReleaseManifest Manifest, byte[] ManifestBytes, byte[] SignatureBytes, string PackagePath);

/// <summary>A package taken in by <see cref="DesktopPackageStore.Publish"/>.</summary>
public sealed record DesktopPublishResult(ReleaseManifest Manifest, bool AlreadyOffered);

/// <summary>
/// The HC360 Desktop package the Server Node offers to enrolled workstations (SRS DED-18), in the data folder:
/// <c>updates\desktop\&lt;version&gt;-&lt;sha&gt;\</c> holds a package (<c>package.zip</c>, <c>manifest.json</c>,
/// <c>manifest.sig</c>), and <c>updates\desktop\current</c> names the one offered. A package is taken in only once it
/// verifies (<see cref="ReleasePackages"/>): signed by a trusted HyFib key, for HC360 Desktop, the size and SHA-256 its
/// manifest says, not older than the one offered, and for a server of this version. It is checked again before it is
/// offered, so a package changed on disk afterwards is withdrawn, not served. Install-HC360Server.ps1 takes in the package
/// that comes with the server's release, and <c>desktop publish</c> any other.
/// </summary>
public sealed partial class DesktopPackageStore
{
    public const string PackageFile = "package.zip";
    public const string ManifestFile = "manifest.json";
    public const string SignatureFile = "manifest.sig";
    private const string CurrentFile = "current";

    private readonly ReleaseTrust _trust;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private Checked? _checked;
    private string? _lastProblem;

    public DesktopPackageStore(DataDirectories directories, ReleaseTrust trust, ILogger<DesktopPackageStore> logger)
        : this(DirectoryIn(directories), trust, logger)
    {
    }

    internal DesktopPackageStore(string directory, ReleaseTrust trust, ILogger? logger = null)
    {
        Directory = directory;
        _trust = trust;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><c>updates\desktop</c> under the data folder.</summary>
    public string Directory { get; }

    public static string DirectoryIn(DataDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        return Path.Combine(directories.Root, "updates", "desktop");
    }

    /// <summary>
    /// Takes in the package at <paramref name="packagePath"/>, with its manifest and signature beside it
    /// (<see cref="ReleasePackages.ManifestPathFor"/>), and offers it from now on. Nothing changes when it does not verify.
    /// </summary>
    /// <exception cref="ReleaseRejectedException">The package does not verify, is older than the one offered, or needs a newer server.</exception>
    /// <exception cref="FileNotFoundException">The package or its manifest is missing.</exception>
    public DesktopPublishResult Publish(string packagePath, string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        var manifestPath = ReleasePackages.ManifestPathFor(packagePath);
        var signaturePath = ReleasePackages.SignaturePathFor(packagePath);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException($"There is no package at {packagePath}.", packagePath);
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"There is no manifest beside the package ({Path.GetFileName(manifestPath)}): only a release's packages can be offered.", manifestPath);
        }

        var manifestBytes = ReadSmall(manifestPath, ReleasePackages.MaxManifestBytes);
        var signatureBytes = File.Exists(signaturePath) ? ReadSmall(signaturePath, ReleasePackages.MaxSignatureBytes) : [];
        var manifest = ReleasePackages.ReadManifest(manifestBytes, signatureBytes, _trust.Keys, ReleasePackages.DesktopProduct);
        ReleasePackages.CheckServer(manifest, serverVersion);

        // The package is held open, so that nobody can change it, from its check to the end of the copy, and the copy is
        // checked again. Nothing is written before the package and the manifest both verify.
        using var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ReleasePackages.CheckPackage(source, manifest);

        var current = TryReadOffer(out _);
        if (current is not null)
        {
            if (manifest.ReleaseVersion < current.Manifest.ReleaseVersion)
            {
                throw new ReleaseRejectedException(
                    ReleaseProblem.NotNewer, $"HC360 Desktop {manifest.Version} is older than {current.Manifest.Version}, which the server offers already; workstations never go back a version.");
            }

            if (manifest.ReleaseVersion == current.Manifest.ReleaseVersion)
            {
                if (string.Equals(manifest.Sha256, current.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new DesktopPublishResult(manifest, AlreadyOffered: true);
                }

                throw new ReleaseRejectedException(
                    ReleaseProblem.NotNewer, $"The server offers another package of HC360 Desktop {manifest.Version} already; workstations that have it would not take this one.");
            }
        }

        System.IO.Directory.CreateDirectory(Directory);
        var name = $"{manifest.ReleaseVersion}-{manifest.Sha256[..12].ToLowerInvariant()}";
        var target = Path.Combine(Directory, name);
        var partial = Path.Combine(Directory, $"{name}.partial-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(partial);
        try
        {
            source.Position = 0;
            using (var copy = new FileStream(Path.Combine(partial, PackageFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                source.CopyTo(copy);
                copy.Flush(flushToDisk: true);
                ReleasePackages.CheckPackage(copy, manifest);
            }

            File.WriteAllBytes(Path.Combine(partial, ManifestFile), manifestBytes);
            File.WriteAllBytes(Path.Combine(partial, SignatureFile), signatureBytes);
            if (System.IO.Directory.Exists(target))
            {
                System.IO.Directory.Delete(target, recursive: true);
            }

            System.IO.Directory.Move(partial, target);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        var pointer = Path.Combine(Directory, CurrentFile);
        File.WriteAllText(pointer + ".tmp", name);
        File.Move(pointer + ".tmp", pointer, overwrite: true);
        RemoveOthers(name);
        return new DesktopPublishResult(manifest, AlreadyOffered: false);
    }

    /// <summary>
    /// The package the server offers, checked again: signature, product, size and SHA-256 (remembered while the file
    /// stays as it is), and this server's version. Null when there is none, or it no longer verifies.
    /// </summary>
    public async Task<DesktopOffer?> GetOfferAsync(string serverVersion, CancellationToken cancellationToken)
    {
        var (offer, problem) = await CheckAsync(serverVersion, cancellationToken);
        return problem is null ? offer : null;
    }

    /// <summary>What the server offers, or why it offers nothing: for Administration › Devices.</summary>
    public async Task<(DesktopOffer? Offer, string? Problem)> CheckAsync(string serverVersion, CancellationToken cancellationToken)
    {
        DesktopOffer? offer;
        string? problem;
        try
        {
            offer = TryReadOffer(out problem);
            if (offer is not null)
            {
                ReleasePackages.CheckServer(offer.Manifest, serverVersion);
                await CheckPackageAsync(offer, cancellationToken);
            }
        }
        catch (ReleaseRejectedException ex)
        {
            offer = null;
            problem = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            offer = null;
            problem = $"The package could not be read: {ex.Message}";
        }

        if (!string.Equals(problem, Interlocked.Exchange(ref _lastProblem, problem), StringComparison.Ordinal) && problem is not null)
        {
            _logger.LogWarning("The server offers no HC360 Desktop update to workstations: {Problem}", problem);
        }

        return (offer, problem);
    }

    // The offered package's manifest and signature, verified; null with no problem when nothing was ever taken in.
    private DesktopOffer? TryReadOffer(out string? problem)
    {
        problem = null;
        var pointer = Path.Combine(Directory, CurrentFile);
        if (!File.Exists(pointer))
        {
            return null;
        }

        var name = File.ReadAllText(pointer).Trim();
        if (!FolderName().IsMatch(name))
        {
            problem = "The record of the package offered is damaged. Put the package on the server again (desktop publish).";
            return null;
        }

        var folder = Path.Combine(Directory, name);
        var packagePath = Path.Combine(folder, PackageFile);
        var manifestPath = Path.Combine(folder, ManifestFile);
        var signaturePath = Path.Combine(folder, SignatureFile);
        if (!File.Exists(packagePath) || !File.Exists(manifestPath))
        {
            problem = "The package offered is missing from the server's data folder. Put it on the server again (desktop publish).";
            return null;
        }

        var manifestBytes = ReadSmall(manifestPath, ReleasePackages.MaxManifestBytes);
        var signatureBytes = File.Exists(signaturePath) ? ReadSmall(signaturePath, ReleasePackages.MaxSignatureBytes) : [];
        try
        {
            var manifest = ReleasePackages.ReadManifest(manifestBytes, signatureBytes, _trust.Keys, ReleasePackages.DesktopProduct);
            return new DesktopOffer(manifest, manifestBytes, signatureBytes, packagePath);
        }
        catch (ReleaseRejectedException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    // The package's size and SHA-256, hashed again whenever the file changes.
    private async Task CheckPackageAsync(DesktopOffer offer, CancellationToken cancellationToken)
    {
        var file = new FileInfo(offer.PackagePath);
        var stamp = new Checked(offer.PackagePath, file.Length, file.LastWriteTimeUtc, offer.Manifest.Sha256);
        if (Volatile.Read(ref _checked) == stamp)
        {
            return;
        }

        await _checking.WaitAsync(cancellationToken);
        try
        {
            if (_checked == stamp)
            {
                return;
            }

            await using var package = new FileStream(offer.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await ReleasePackages.CheckPackageAsync(package, offer.Manifest, cancellationToken);
            Volatile.Write(ref _checked, stamp);
        }
        finally
        {
            _checking.Release();
        }
    }

    private void RemoveOthers(string keep)
    {
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
        {
            if (!string.Equals(Path.GetFileName(folder), keep, StringComparison.OrdinalIgnoreCase))
            {
                // One being downloaded stays until the next package is taken in.
                TryDelete(folder);
            }
        }
    }

    private void TryDelete(string folder)
    {
        try
        {
            System.IO.Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("An earlier HC360 Desktop package in {Folder} is still in use; it is removed with the next one", folder);
        }
    }

    private static byte[] ReadSmall(string path, int max)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > max)
        {
            throw new ReleaseRejectedException(ReleaseProblem.UnreadableManifest, $"{Path.GetFileName(path)} is too large to be a release's manifest or signature.");
        }

        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,80}$")]
    private static partial Regex FolderName();

    private sealed record Checked(string Path, long Length, DateTime WrittenAt, string Sha256);
}
