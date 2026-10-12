using System.Diagnostics;
using HyFib.HealthCare360.WindowsInterop.Updates;

namespace HyFib.HealthCare360.DesktopUpdater;

/// <summary>
/// "Restart to update", as the signed-in user: once that HC360 Desktop has closed, starts the "HC360 Desktop update" task
/// (Install-HC360Desktop.ps1 lets users start it, and nothing else), waits for it to record its run, and starts HC360
/// Desktop again, the new version when the update installed and the same one otherwise. Users cannot change what the
/// task runs, and it trusts nothing they can write, so starting it gives them nothing more.
/// </summary>
internal static class RestartFlow
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);

    public static int Run(int desktopProcessId, string installDirectory, string statusPath, TimeProvider time)
    {
        try
        {
            using var desktop = Process.GetProcessById(desktopProcessId);
            desktop.WaitForExit(CloseTimeout);
        }
        catch (ArgumentException)
        {
            // It has closed already.
        }

        var started = time.GetUtcNow();
        if (StartTask())
        {
            WaitForRun(() => DesktopUpdateStatus.Read(statusPath), started, InstallTimeout, time, Thread.Sleep);
        }

        var exe = Path.Combine(installDirectory, DesktopUpdateLayout.DesktopExe);
        if (File.Exists(exe))
        {
            using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installDirectory });
        }

        return 0;
    }

    /// <summary>The task's record of a run that began after <paramref name="since"/>, or null when none came in time.</summary>
    internal static DesktopUpdateStatus? WaitForRun(
        Func<DesktopUpdateStatus?> read, DateTimeOffset since, TimeSpan timeout, TimeProvider time, Action<TimeSpan> sleep)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            // A little earlier than the start: the task's clock is this computer's, and it records the end of its run.
            if (read() is { } status && status.At >= since.AddSeconds(-2))
            {
                return status;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return null;
            }

            sleep(TimeSpan.FromSeconds(1));
        }
    }

    private static bool StartTask()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/Run");
        start.ArgumentList.Add("/TN");
        start.ArgumentList.Add(DesktopUpdateLayout.TaskPath + DesktopUpdateLayout.TaskName);
        using var schtasks = Process.Start(start);
        if (schtasks is null)
        {
            return false;
        }

        schtasks.StandardOutput.ReadToEnd();
        schtasks.StandardError.ReadToEnd();
        return schtasks.WaitForExit(TimeSpan.FromSeconds(30)) && schtasks.ExitCode == 0;
    }
}

/// <summary>
/// The updater replaces itself with the copy a new version of HC360 Desktop brings: a running program file can be renamed,
/// though not overwritten, so it moves aside (<c>.old</c>, removed at the next run) and the new one is copied in.
/// </summary>
internal static class SelfUpdate
{
    public static void RemoveLeftover(string? processPath)
    {
        if (processPath is null)
        {
            return;
        }

        try
        {
            File.Delete(processPath + ".old");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use; the next run removes it.
        }
    }

    /// <summary>What it did, or null when the updater is the same as the new version's (or runs from elsewhere).</summary>
    public static string? TryReplace(string? processPath, string updaterDirectory, string installDirectory)
    {
        var bundled = Path.Combine(installDirectory, DesktopUpdateLayout.UpdaterExe);
        if (processPath is null || !File.Exists(bundled)
            || !string.Equals(Path.GetDirectoryName(processPath), Path.TrimEndingDirectorySeparator(updaterDirectory), StringComparison.OrdinalIgnoreCase)
            || SameContent(processPath, bundled))
        {
            return null;
        }

        var aside = processPath + ".old";
        try
        {
            File.Delete(aside);
            File.Move(processPath, aside);
            try
            {
                File.Copy(bundled, processPath);
            }
            catch
            {
                File.Move(aside, processPath);
                throw;
            }

            return "The updater was replaced by the new version's.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The updater could not be replaced by the new version's ({ex.Message}); the next run tries again.";
        }
    }

    internal static bool SameContent(string a, string b)
    {
        var first = new FileInfo(a);
        var second = new FileInfo(b);
        if (first.Length != second.Length)
        {
            return false;
        }

        using var x = first.OpenRead();
        using var y = second.OpenRead();
        return System.Security.Cryptography.SHA256.HashData(x).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(y));
    }
}
