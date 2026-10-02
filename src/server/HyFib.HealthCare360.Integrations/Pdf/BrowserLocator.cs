using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HyFib.HealthCare360.Integrations.Pdf;

/// <summary>Finds the browser that makes PDFs: the configured one, else Microsoft Edge, else Google Chrome.</summary>
internal static class BrowserLocator
{
    /// <summary>The browser's full path, or null when there is none (or the configured path does not exist).</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        return OperatingSystem.IsWindows() ? Candidates().FirstOrDefault(File.Exists) : null;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> Candidates()
    {
        string[] programFiles =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ];
        foreach (var root in programFiles.Where(r => r.Length > 0))
        {
            yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
        }

        if (RegisteredPath("msedge.exe") is { } edge)
        {
            yield return edge;
        }

        // Chrome may be installed for one user only; the service account then has none, but a development machine does.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in programFiles.Append(localAppData).Where(r => r.Length > 0))
        {
            yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
        }

        if (RegisteredPath("chrome.exe") is { } chrome)
        {
            yield return chrome;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? RegisteredPath(string executable) =>
        Registry.GetValue($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}", null, null) is string { Length: > 0 } path
            ? path.Trim('"')
            : null;
}
