using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyFib.HealthCare360.WindowsInterop.Updates;

/// <summary>
/// Where HC360 Desktop's self-update keeps its files (SRS DED-18), shared by HC360 Desktop, which downloads and stages a
/// package as the signed-in user, and the updater, which installs it as SYSTEM.
/// <list type="bullet">
/// <item>Staging, <c>%ProgramData%\HyFib\HC360Desktop\Updates</c>: every user may write here, so the updater trusts
/// nothing in it that it has not verified itself, and never writes or deletes there.</item>
/// <item>The updater, <c>&lt;install folder&gt; Updater</c> (<c>%ProgramFiles%\HyFib\HC360 Desktop Updater</c>): only
/// administrators may change it. The "HC360 Desktop update" task runs it from there, and it records what it did there
/// (<see cref="StatusFile"/>), where users can read it but not change it.</item>
/// <item>While installing: <c>&lt;install folder&gt;.new</c> (the new version, unpacked), then
/// <c>&lt;install folder&gt;.previous</c> (the version before, kept for a rollback).</item>
/// </list>
/// </summary>
public static class DesktopUpdateLayout
{
    public const string DesktopExe = "HC360Desktop.exe";
    public const string UpdaterExe = "HC360DesktopUpdater.exe";

    /// <summary>The SYSTEM scheduled task Install-HC360Desktop.ps1 registers, which installs a staged package.</summary>
    public const string TaskPath = @"\HyFib\";

    public const string TaskName = "HC360 Desktop update";

    public const string StatusFile = "status.json";

    /// <summary>The staged package's files, as HC360 Desktop downloaded them from the Server Node.</summary>
    public const string PackageFile = "desktop.zip";

    public const string ManifestFile = "desktop" + ReleasePackages.ManifestSuffix;

    public const string SignatureFile = "desktop" + ReleasePackages.SignatureSuffix;

    /// <summary><c>%ProgramData%\HyFib\HC360Desktop</c>: the machine policy, the printers and the staged update.</summary>
    public static string MachineDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyFib", "HC360Desktop");

    public static string StagingDirectory { get; } = Path.Combine(MachineDirectory, "Updates");

    public static string PolicyPath { get; } = Path.Combine(MachineDirectory, "policy.json");

    public static string UpdaterDirectoryFor(string installDirectory) => Trim(installDirectory) + " Updater";

    public static string NewDirectoryFor(string installDirectory) => Trim(installDirectory) + ".new";

    public static string PreviousDirectoryFor(string installDirectory) => Trim(installDirectory) + ".previous";

    /// <summary>
    /// Whether IT allows HC360 Desktop to update itself on this computer: yes, unless the machine policy says
    /// <c>"autoUpdate": false</c> (Install-HC360Desktop.ps1 -NoAutoUpdate), for computers whose software IT distributes
    /// itself. A policy that cannot be read leaves it on, as HC360 Desktop does for the rest of the policy.
    /// </summary>
    public static bool AutoUpdateAllowed(string policyPath)
    {
        try
        {
            if (!File.Exists(policyPath) || new FileInfo(policyPath).Length > 1024 * 1024)
            {
                return true;
            }

            // Windows PowerShell writes the policy with a byte order mark.
            var bytes = File.ReadAllBytes(policyPath).AsMemory();
            using var policy = JsonDocument.Parse(bytes.Span.StartsWith("﻿"u8) ? bytes[3..] : bytes, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (policy.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            foreach (var property in policy.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "autoUpdate", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.False)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    private static string Trim(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
}

/// <summary>What the updater's last run did.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DesktopUpdateOutcome>))]
public enum DesktopUpdateOutcome
{
    /// <summary>A new version was installed.</summary>
    Installed,

    /// <summary>Nothing is staged, or what is staged is not newer than the version installed.</summary>
    UpToDate,

    /// <summary>A verified package waits until HC360 Desktop is closed for every user.</summary>
    Waiting,

    /// <summary>IT turned self-update off (<see cref="DesktopUpdateLayout.AutoUpdateAllowed"/>).</summary>
    TurnedOff,

    /// <summary>The staged package did not verify, and was not installed.</summary>
    Refused,

    /// <summary>Installing failed; the version before was put back.</summary>
    Failed,
}

/// <summary>The updater's record of its last run, in <see cref="DesktopUpdateLayout.StatusFile"/>.</summary>
public sealed record DesktopUpdateStatus(DateTimeOffset At, DesktopUpdateOutcome Outcome, string? Version, string Message)
{
    public static DesktopUpdateStatus? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdatesJson.Default.DesktopUpdateStatus) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, UpdatesJson.Default.DesktopUpdateStatus));
        File.Move(temp, path, overwrite: true);
    }
}
