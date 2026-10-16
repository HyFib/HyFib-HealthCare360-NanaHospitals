using System.IO;
using HyFib.HealthCare360.WindowsInterop.Updates;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// Stages the HC360 Desktop package the Server Node offers (SRS DED-18) in the machine's staging folder
/// (<see cref="DesktopUpdateLayout.StagingDirectory"/>), for the updater to install as SYSTEM. Only what verifies is
/// staged: the manifest is signed by a key built into HC360 (<see cref="ReleaseKeys"/>), is for HC360 Desktop, and is
/// newer than the version running; the package downloaded has its size and SHA-256. The updater checks all of it again,
/// because any user may write in the staging folder.
/// </summary>
internal sealed class UpdateStager(string stagingDirectory, IReadOnlyList<ReleaseKey> trustedKeys, string installedVersion)
{
    private const string PartialSuffix = ".partial";

    private (long Length, DateTime WrittenAt, string Sha256)? _checked;

    private string ManifestPath => Path.Combine(stagingDirectory, DesktopUpdateLayout.ManifestFile);

    private string SignaturePath => Path.Combine(stagingDirectory, DesktopUpdateLayout.SignatureFile);

    private string PackagePath => Path.Combine(stagingDirectory, DesktopUpdateLayout.PackageFile);

    /// <summary>
    /// The staged package's manifest, when a package newer than this version is staged and verifies; null otherwise.
    /// </summary>
    public ReleaseManifest? Ready()
    {
        try
        {
            if (!File.Exists(ManifestPath) || !File.Exists(PackagePath))
            {
                return null;
            }

            var manifest = ReleasePackages.ReadManifest(ReadSmall(ManifestPath), ReadSmall(SignaturePath), trustedKeys, ReleasePackages.DesktopProduct);
            ReleasePackages.CheckNewer(manifest, installedVersion);
            var file = new FileInfo(PackagePath);
            if (_checked != (file.Length, file.LastWriteTimeUtc, manifest.Sha256))
            {
                using var package = new FileStream(PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                ReleasePackages.CheckPackage(package, manifest);
                _checked = (file.Length, file.LastWriteTimeUtc, manifest.Sha256);
            }

            return manifest;
        }
        catch (Exception ex) when (ex is ReleaseRejectedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stages the package <paramref name="manifest"/> describes, once its signature verifies and it is newer than this
    /// version: <paramref name="download"/> appends the package to the file it is given, from that file's length (a
    /// download that was cut off goes on where it stopped), or writes it from the start after emptying the file. Null when
    /// the package is not newer than this version, so there is nothing to stage.
    /// </summary>
    /// <exception cref="ReleaseRejectedException">The manifest or the package downloaded did not verify; nothing was staged.</exception>
    public async Task<ReleaseManifest?> StageAsync(
        byte[] manifest, byte[] signature, Func<FileStream, ReleaseManifest, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        var offered = ReleasePackages.ReadManifest(manifest, signature, trustedKeys, ReleasePackages.DesktopProduct);
        try
        {
            ReleasePackages.CheckNewer(offered, installedVersion);
        }
        catch (ReleaseRejectedException)
        {
            return null;
        }

        if (Ready() is { } staged && staged.ReleaseVersion == offered.ReleaseVersion && string.Equals(staged.Sha256, offered.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return staged;
        }

        Directory.CreateDirectory(stagingDirectory);
        var partialPath = Path.Combine(stagingDirectory, $"desktop-{offered.Sha256[..12].ToLowerInvariant()}{PartialSuffix}");
        RemovePartials(except: partialPath);

        // Held without sharing while it downloads, so a second user of this computer does not write it at the same time.
        await using (var partial = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
        {
            if (partial.Length > offered.Size)
            {
                partial.SetLength(0);
            }

            partial.Position = partial.Length;
            if (partial.Length < offered.Size)
            {
                await download(partial, offered, cancellationToken);
                await partial.FlushAsync(cancellationToken);
            }

            try
            {
                await ReleasePackages.CheckPackageAsync(partial, offered, cancellationToken);
            }
            catch (ReleaseRejectedException)
            {
                partial.SetLength(0);
                throw;
            }
        }

        // The updater sees no manifest while the package changes, so it never pairs an old manifest with a new package.
        File.Delete(ManifestPath);
        File.Move(partialPath, PackagePath, overwrite: true);
        WriteAtomically(SignaturePath, signature);
        WriteAtomically(ManifestPath, manifest);
        _checked = null;
        Log.Information("HC360 Desktop {Version} is downloaded and verified, ready to install", offered.Version);
        return Ready();
    }

    /// <summary>
    /// Removes what is staged once it is not newer than this version (the update was installed), and downloads that were
    /// cut off long ago. A file another user staged may be left; the next of that user's runs removes it.
    /// </summary>
    public void CleanUp(TimeSpan partialAge)
    {
        try
        {
            if (File.Exists(ManifestPath) && Ready() is null && StagedIsNotNewer())
            {
                foreach (var path in new[] { ManifestPath, SignaturePath, PackagePath })
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(stagingDirectory))
            {
                foreach (var partial in Directory.EnumerateFiles(stagingDirectory, "*" + PartialSuffix))
                {
                    if (File.GetLastWriteTimeUtc(partial) < DateTime.UtcNow - partialAge)
                    {
                        File.Delete(partial);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Information(ex, "The staged update could not be removed");
        }
    }

    private bool StagedIsNotNewer()
    {
        try
        {
            var manifest = ReleasePackages.ReadManifest(ReadSmall(ManifestPath), ReadSmall(SignaturePath), trustedKeys, ReleasePackages.DesktopProduct);
            ReleasePackages.CheckNewer(manifest, installedVersion);
            return false;
        }
        catch (ReleaseRejectedException)
        {
            // Not newer, or never valid: either way nothing to keep.
            return true;
        }
    }

    private void RemovePartials(string except)
    {
        foreach (var partial in Directory.EnumerateFiles(stagingDirectory, "*" + PartialSuffix))
        {
            if (!string.Equals(partial, except, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another user's, still downloading.
                }
            }
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] ReadSmall(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > ReleasePackages.MaxManifestBytes)
        {
            throw new ReleaseRejectedException(ReleaseProblem.UnreadableManifest, $"{Path.GetFileName(path)} is too large.");
        }

        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }
}
