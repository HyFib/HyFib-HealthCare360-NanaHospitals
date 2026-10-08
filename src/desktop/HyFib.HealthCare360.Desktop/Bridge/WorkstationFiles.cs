using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// What HC360 Desktop keeps for the computer rather than the Windows account, under
/// <c>%ProgramData%\HyFib\HC360Desktop</c>: the printers and the barcode scanner, saved in <c>Workstation</c> for
/// everyone who uses the computer, and the read-only machine policy (<c>policy.json</c>) in which IT may fix them.
/// </summary>
internal static class WorkstationFiles
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyFib", "HC360Desktop");

    public static string Folder { get; } = Path.Combine(Root, "Workstation");

    public static string PolicyPath { get; } = Path.Combine(Root, "policy.json");

    /// <summary>Replaces the file in one step (a temporary file moved over it), creating the folder first.</summary>
    public static void Write(string folder, string path, string contents)
    {
        EnsureFolder(folder);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Text the staff app sent, without control characters, trimmed and cut to <paramref name="max"/>.</summary>
    public static string? Clean(string? value, int max)
    {
        var text = value is null ? null : new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    /// <summary>
    /// The machine policy's <paramref name="entry"/> (<c>printers</c>, <c>scanner</c>) when IT set one; null when there
    /// is no policy, it has no such entry, or it cannot be read.
    /// </summary>
    public static T? ReadPolicy<T>(string? policyPath, string entry, JsonSerializerOptions json)
        where T : class
    {
        if (policyPath is null || !File.Exists(policyPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(entry, out var value)
                   && value.ValueKind == JsonValueKind.Object
                ? value.Deserialize<T>(json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "The {Entry} in the machine policy could not be read", entry);
            return null;
        }
    }

    /// <summary>
    /// Creates the folder so every Windows user on the computer may change the files in it (Users: modify, inherited).
    /// The installer creates it the same way; a folder that already exists is left as it is.
    /// </summary>
    private static void EnsureFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new DirectoryInfo(folder).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(security);
    }
}
