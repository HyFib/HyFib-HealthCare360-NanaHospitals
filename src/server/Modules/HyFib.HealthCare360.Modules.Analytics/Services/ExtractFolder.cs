using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// The folder BI extracts are written to (BI-05): <c>HealthCare360:Analytics:ExtractDirectory</c> in the server's
/// configuration (a relative path is taken from the data folder), by default <c>bi-extracts</c> in the data folder. Each
/// facility has a sub-folder named by its code, so a BI
/// tool reading the folder can combine the facilities' files, and <see cref="RefreshLogFile"/> at the top lists when
/// each extract was last refreshed. IT shares the folder, read-only, with the BI tool (for Power BI, the on-premises data
/// gateway's account). Only the server's settings choose the folder; the staff app chooses file names, which are checked
/// (<see cref="IsValidName"/>), so a file can never be written outside it.
/// </summary>
internal sealed partial class ExtractFolder(IConfiguration configuration, DataDirectories directories)
{
    public const string ConfigurationKey = "HealthCare360:Analytics:ExtractDirectory";
    public const string DefaultFolderName = "bi-extracts";
    public const string RefreshLogFile = "refresh-log.csv";
    public const string DeviceNameMessage = "Windows keeps this name for a device. Choose another, for example daily-revenue.";

    /// <summary>Where files are written before they replace the published ones, on the same disk so the move is atomic.</summary>
    private const string WorkFolder = ".writing";

    private const int MoveAttempts = 5;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(200);

    // A service's working directory is System32, so a relative path is taken from the data folder instead.
    public string Root { get; } = configuration[ConfigurationKey] is { Length: > 0 } configured
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), directories.Root)
        : Path.Combine(directories.Root, DefaultFolderName);

    /// <summary>
    /// A file name an extract may use: lower-case letters, digits and dashes, starting with a letter or digit, and not a
    /// name Windows keeps for a device (<see cref="IsDeviceName"/>).
    /// </summary>
    public static bool IsValidName(string? name) => HasValidForm(name) && !IsDeviceName(name);

    /// <summary>Lower-case letters, digits and dashes, starting with a letter or digit, at most 64 characters.</summary>
    public static bool HasValidForm(string? name) => name is { Length: > 0 and <= Domain.ReportSchedule.MaxExtractNameLength } && NamePattern().IsMatch(name);

    /// <summary><c>con</c>, <c>nul</c>, <c>com1</c> and the like: Windows opens a device, not a file, by such a name.</summary>
    public static bool IsDeviceName(string? name) => name is not null && DeviceNamePattern().IsMatch(name);

    /// <summary>The facility's sub-folder (<see cref="FolderNameFor"/>); a code that is not letters and digits is refused.</summary>
    public string FacilityFolder(string facilityCode) =>
        facilityCode is { Length: > 0 } && facilityCode.All(char.IsAsciiLetterOrDigit)
            ? Path.Combine(Root, FolderNameFor(facilityCode))
            : throw new ArgumentException($"'{facilityCode}' is not a facility code.", nameof(facilityCode));

    public string FilePath(string facilityCode, string extractName) => Path.Combine(FacilityFolder(facilityCode), FileName(extractName));

    public static string FileName(string extractName) => IsValidName(extractName)
        ? extractName + ".csv"
        : throw new ArgumentException($"'{extractName}' is not a valid extract name.", nameof(extractName));

    /// <summary>
    /// Replaces <paramref name="path"/> (inside <see cref="Root"/>) with <paramref name="content"/> whole: the file is
    /// written aside and moved over the old one, so a BI tool reading it never sees half a file. A reader holding the
    /// old file open delays the move; it is tried again for about a second.
    /// </summary>
    /// <exception cref="IOException">The folder cannot be written (missing share, no permission, disk full).</exception>
    /// <exception cref="UnauthorizedAccessException">The server's account may not write the folder.</exception>
    public async Task WriteAsync(string path, byte[] content, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Extracts are written inside the extract folder only.", nameof(path));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var work = Directory.CreateDirectory(Path.Combine(Root, WorkFolder)).FullName;
        var temp = Path.Combine(work, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, full, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < MoveAttempts)
                {
                    await Task.Delay(MoveRetryDelay, ct);
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Removes the file of an extract that was deleted or renamed, so a BI tool reports it missing instead of reading
    /// data that is no longer refreshed. A file that cannot be removed is left; the refresh log no longer lists it.
    /// </summary>
    public void Remove(string facilityCode, string extractName)
    {
        if (IsValidName(extractName))
        {
            TryDelete(FilePath(facilityCode, extractName));
        }
    }

    /// <summary>Null when the server can write the folder; otherwise what is wrong, for the person setting it up.</summary>
    public string? Check()
    {
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(Root, WorkFolder)).FullName;
            var probe = Path.Combine(work, $"check-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"HC360 cannot write to {Root}: {ex.Message}";
        }
    }

    /// <summary>
    /// The name of a facility's sub-folder, as the refresh log and the audit show it: its code (2–6 upper-case letters),
    /// with an underscore after a code Windows keeps for a device (CON, PRN, AUX, NUL), which cannot name a folder: CON_.
    /// </summary>
    public static string FolderNameFor(string facilityCode) =>
        IsDeviceName(facilityCode.ToLowerInvariant()) ? facilityCode + "_" : facilityCode;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; the next write uses a new name.
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^(con|prn|aux|nul|com[0-9]|lpt[0-9])$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeviceNamePattern();
}
