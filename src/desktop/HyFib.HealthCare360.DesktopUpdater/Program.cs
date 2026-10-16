using System.Diagnostics;
using System.Globalization;
using HyFib.HealthCare360.DesktopUpdater;
using HyFib.HealthCare360.WindowsInterop.Updates;

// HC360DesktopUpdater.exe apply [--install-dir <folder>]
//     As SYSTEM, from the "HC360 Desktop update" task: installs the package HC360 Desktop staged, when it verifies and
//     HC360 Desktop is not running (DesktopInstaller), and records what it did in status.json beside this program.
// HC360DesktopUpdater.exe restart --pid <id> [--install-dir <folder>]
//     As the signed-in user, from HC360 Desktop's "Restart to update": waits for that HC360 Desktop to close, has the task
//     install the update, and starts HC360 Desktop again (RestartFlow).
var options = Options(args);
var updaterDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
var installDirectory = options.TryGetValue("install-dir", out var given)
    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(given))
    : InstallDirectoryFor(updaterDirectory);
var log = new UpdaterLog(Path.Combine(updaterDirectory, "update.log"));
var statusPath = Path.Combine(updaterDirectory, DesktopUpdateLayout.StatusFile);

try
{
    switch (args.FirstOrDefault())
    {
        case "apply":
            SelfUpdate.RemoveLeftover(Environment.ProcessPath);
            var installer = new DesktopInstaller(new InstallerContext(
                installDirectory,
                DesktopUpdateLayout.StagingDirectory,
                ReleaseKeys.Trusted,
                () => DesktopUpdateLayout.AutoUpdateAllowed(DesktopUpdateLayout.PolicyPath),
                () => RunningDesktop.IsRunning(installDirectory),
                InstallerContext.ProductVersionOf,
                TimeProvider.System));
            var status = installer.Apply();

            // After an update, and on later runs if that did not work: the updater the installed version brings.
            if (SelfUpdate.TryReplace(Environment.ProcessPath, updaterDirectory, installDirectory) is { } replaced)
            {
                log.Write(replaced);
            }

            status.Write(statusPath);
            log.Write($"{status.Outcome}: {status.Message}");
            return status.Outcome is DesktopUpdateOutcome.Failed or DesktopUpdateOutcome.Refused ? 3 : 0;

        case "restart" when options.TryGetValue("pid", out var pid) && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var processId):
            return RestartFlow.Run(processId, installDirectory, statusPath, TimeProvider.System);

        default:
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    log.Write($"Failed: {ex}");
    return 1;
}

// <install folder> Updater → <install folder>.
static string InstallDirectoryFor(string updaterDirectory) =>
    updaterDirectory.EndsWith(" Updater", StringComparison.OrdinalIgnoreCase) ? updaterDirectory[..^" Updater".Length] : updaterDirectory;

static Dictionary<string, string> Options(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 1; i + 1 < args.Length; i += 2)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            result[args[i][2..]] = args[i + 1];
        }
    }

    return result;
}

/// <summary>Whether HC360 Desktop runs, for any user, from the install folder.</summary>
internal static class RunningDesktop
{
    public static bool IsRunning(string installDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(installDirectory) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DesktopUpdateLayout.DesktopExe)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { } path || path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Its path cannot be read: count it as running from here, to be safe.
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>The updater's log beside it: one line a run, kept under half a megabyte.</summary>
internal sealed class UpdaterLog(string path)
{
    private const long MaxBytes = 512 * 1024;

    public void Write(string message)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The user-mode restart cannot write here, and needs no log.
        }
    }
}
