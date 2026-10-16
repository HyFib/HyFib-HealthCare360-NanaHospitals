using System.Diagnostics;
using System.IO.Compression;
using HyFib.HealthCare360.WindowsInterop.Updates;

namespace HyFib.HealthCare360.DesktopUpdater;

/// <summary>Where the installer works, and what it asks of the computer (replaced in the tests).</summary>
/// <param name="InstallDirectory">HC360 Desktop's folder, <c>%ProgramFiles%\HyFib\HC360 Desktop</c>.</param>
/// <param name="StagingDirectory">Where HC360 Desktop staged the package (<see cref="DesktopUpdateLayout.StagingDirectory"/>).</param>
/// <param name="TrustedKeys">The release keys built in (<see cref="ReleaseKeys.Trusted"/>).</param>
/// <param name="AutoUpdateAllowed">Whether IT allows self-update (<see cref="DesktopUpdateLayout.AutoUpdateAllowed"/>).</param>
/// <param name="IsDesktopRunning">Whether HC360 Desktop runs from <see cref="InstallDirectory"/> for any user.</param>
/// <param name="ReadVersion">The product version of a program file, or null.</param>
internal sealed record InstallerContext(
    string InstallDirectory,
    string StagingDirectory,
    IReadOnlyList<ReleaseKey> TrustedKeys,
    Func<bool> AutoUpdateAllowed,
    Func<bool> IsDesktopRunning,
    Func<string, string?> ReadVersion,
    TimeProvider Time)
{
    /// <summary>How often a folder move that fails because a file is still in use is tried, a second apart.</summary>
    public int MoveAttempts { get; init; } = 5;

    public TimeSpan MoveRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Called between the steps, so the tests can fail one.</summary>
    public Action<string> Step { get; init; } = _ => { };

    public static string? ProductVersionOf(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        return version?.Split('+')[0].Trim();
    }
}

/// <summary>
/// Installs the HC360 Desktop package HC360 Desktop staged (SRS DED-18), as SYSTEM. Users may write where the package is
/// staged, so nothing there is trusted until checked here: the manifest's signature (a key built into this program), the
/// product, that it is newer than the version installed, and the package's size and SHA-256. The package is held open,
/// so nobody can change it, from that check until it is unpacked, and only what was checked is unpacked. Nothing is ever
/// written to or deleted from the staging folder.
/// <list type="number">
/// <item>The package is unpacked to <c>&lt;install folder&gt;.new</c>, and its HC360Desktop.exe must be the manifest's
/// version.</item>
/// <item>Only while HC360 Desktop is not running: the installed folder becomes <c>&lt;install folder&gt;.previous</c>
/// and the new one takes its place. If that fails, the previous version is put back. It is kept afterwards, one version
/// back, for IT to return to by hand.</item>
/// </list>
/// </summary>
internal sealed class DesktopInstaller(InstallerContext context)
{
    private static readonly string[] NotInstalled = ["Install-HC360Desktop.ps1"];

    private string InstallDirectory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.InstallDirectory));

    public DesktopUpdateStatus Apply()
    {
        if (!context.AutoUpdateAllowed())
        {
            return Status(DesktopUpdateOutcome.TurnedOff, null, "IT turned off HC360 Desktop's self-update on this computer (policy.json \"autoUpdate\": false).");
        }

        var manifestPath = Path.Combine(context.StagingDirectory, DesktopUpdateLayout.ManifestFile);
        var signaturePath = Path.Combine(context.StagingDirectory, DesktopUpdateLayout.SignatureFile);
        var packagePath = Path.Combine(context.StagingDirectory, DesktopUpdateLayout.PackageFile);
        if (!File.Exists(manifestPath) || !File.Exists(packagePath))
        {
            return Status(DesktopUpdateOutcome.UpToDate, null, "No update is waiting to be installed.");
        }

        // A link would make SYSTEM read some other file than the one HC360 Desktop staged.
        if (IsLink(context.StagingDirectory) || IsLink(manifestPath) || IsLink(signaturePath) || IsLink(packagePath))
        {
            return Status(DesktopUpdateOutcome.Refused, null, "The staged update is a link to another file, and was not installed.");
        }

        ReleaseManifest manifest;
        try
        {
            manifest = ReleasePackages.ReadManifest(
                ReadSmall(manifestPath, ReleasePackages.MaxManifestBytes), ReadSmall(signaturePath, ReleasePackages.MaxSignatureBytes),
                context.TrustedKeys, ReleasePackages.DesktopProduct);
        }
        catch (ReleaseRejectedException ex)
        {
            return Status(DesktopUpdateOutcome.Refused, null, $"The staged update was not installed: {ex.Message}");
        }

        var installed = context.ReadVersion(Path.Combine(InstallDirectory, DesktopUpdateLayout.DesktopExe));
        if (installed is null)
        {
            return Status(DesktopUpdateOutcome.Failed, manifest.Version, $"HC360 Desktop is not installed in {InstallDirectory}; run Install-HC360Desktop.ps1.");
        }

        try
        {
            ReleasePackages.CheckNewer(manifest, installed);
        }
        catch (ReleaseRejectedException)
        {
            return Status(DesktopUpdateOutcome.UpToDate, installed, $"HC360 Desktop {installed} is installed; the staged {manifest.Version} is not newer.");
        }

        if (context.IsDesktopRunning())
        {
            return Waiting(manifest);
        }

        var newDirectory = DesktopUpdateLayout.NewDirectoryFor(InstallDirectory);
        try
        {
            using var package = OpenHeld(packagePath);
            if (package is null)
            {
                return Status(DesktopUpdateOutcome.Waiting, manifest.Version, $"HC360 Desktop {manifest.Version} is still being downloaded.");
            }

            ReleasePackages.CheckPackage(package, manifest);
            context.Step("checked");
            Unpack(package, newDirectory);
            context.Step("unpacked");
        }
        catch (ReleaseRejectedException ex)
        {
            DeleteQuietly(newDirectory);
            return Status(DesktopUpdateOutcome.Refused, manifest.Version, $"The staged update was not installed: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DeleteQuietly(newDirectory);
            return Status(DesktopUpdateOutcome.Failed, manifest.Version, $"HC360 Desktop {manifest.Version} could not be unpacked: {ex.Message}");
        }

        var unpacked = context.ReadVersion(Path.Combine(newDirectory, DesktopUpdateLayout.DesktopExe));
        if (!ReleaseVersion.TryParse(unpacked, out var unpackedVersion) || unpackedVersion != manifest.ReleaseVersion)
        {
            DeleteQuietly(newDirectory);
            return Status(DesktopUpdateOutcome.Refused, manifest.Version, $"The package's HC360Desktop.exe is {unpacked ?? "missing"}, not {manifest.Version}; it was not installed.");
        }

        return Swap(manifest, installed, newDirectory);
    }

    // The installed folder becomes .previous and the new one takes its place; any failure puts the previous one back.
    private DesktopUpdateStatus Swap(ReleaseManifest manifest, string installed, string newDirectory)
    {
        var previous = DesktopUpdateLayout.PreviousDirectoryFor(InstallDirectory);
        if (context.IsDesktopRunning())
        {
            DeleteQuietly(newDirectory);
            return Waiting(manifest);
        }

        try
        {
            if (Directory.Exists(previous))
            {
                Directory.Delete(previous, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(newDirectory);
            return Status(DesktopUpdateOutcome.Failed, manifest.Version, $"The version kept from before ({previous}) could not be removed: {ex.Message}");
        }

        if (!TryMove(InstallDirectory, previous, out var inUse))
        {
            // A file of the installed version is open: HC360 Desktop started meanwhile. Nothing changed.
            DeleteQuietly(newDirectory);
            return Status(DesktopUpdateOutcome.Waiting, manifest.Version, $"HC360 Desktop {manifest.Version} waits: a file of the installed version is in use ({inUse}).");
        }

        try
        {
            context.Step("moved-aside");
            if (!TryMove(newDirectory, InstallDirectory, out var problem))
            {
                throw new IOException(problem);
            }

            context.Step("installed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restored = RollBack(previous);
            DeleteQuietly(newDirectory);
            return Status(DesktopUpdateOutcome.Failed, installed, restored
                ? $"HC360 Desktop {manifest.Version} could not be installed ({ex.Message}); {installed} was put back."
                : $"HC360 Desktop {manifest.Version} could not be installed ({ex.Message}), and {installed} could not be put back from {previous}: run Install-HC360Desktop.ps1.");
        }

        return Status(DesktopUpdateOutcome.Installed, manifest.Version, $"HC360 Desktop {manifest.Version} was installed, in place of {installed} (kept in {previous}).");
    }

    private bool RollBack(string previous)
    {
        try
        {
            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }

            return TryMove(previous, InstallDirectory, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryMove(string from, string to, out string? problem)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                problem = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= context.MoveAttempts)
                {
                    problem = ex.Message;
                    return false;
                }

                Thread.Sleep(context.MoveRetryDelay);
            }
        }
    }

    // Unpacks what was checked, from the stream held open, into a new folder: no entry may leave it.
    private static void Unpack(FileStream package, string directory)
    {
        DeleteQuietly(directory);
        if (Directory.Exists(directory))
        {
            throw new IOException($"{directory} is left from an earlier attempt and could not be removed.");
        }

        Directory.CreateDirectory(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        package.Position = 0;
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The package has an entry outside its folder ({entry.FullName}).");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var name = Path.GetFileName(target);
            if (NotInstalled.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("MicrosoftEdgeWebview2Setup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    // The package, opened so that nobody can write, rename or delete it while it is held; null while it is being written.
    private static FileStream? OpenHeld(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException) when (File.Exists(path))
        {
            return null;
        }
    }

    private static byte[] ReadSmall(string path, int max)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > max)
        {
            throw new ReleaseRejectedException(ReleaseProblem.UnreadableManifest, $"{Path.GetFileName(path)} is too large.");
        }

        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static bool IsLink(string path)
    {
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null);
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed on the next attempt, before anything is unpacked.
        }
    }

    private DesktopUpdateStatus Waiting(ReleaseManifest manifest) =>
        Status(DesktopUpdateOutcome.Waiting, manifest.Version, $"HC360 Desktop {manifest.Version} is ready, and is installed once HC360 Desktop is closed for every user.");

    private DesktopUpdateStatus Status(DesktopUpdateOutcome outcome, string? version, string message) =>
        new(context.Time.GetUtcNow(), outcome, version, message);
}
