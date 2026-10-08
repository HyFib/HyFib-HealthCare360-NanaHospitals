using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// The barcode scanner on one of this computer's serial (COM) ports: an RS-232 scanner, or a USB scanner set to its
/// serial (virtual COM port) mode. A scanner that types like a keyboard needs no setup. No port means no serial scanner.
/// </summary>
/// <param name="BaudRate">The speed the scanner is set to: 9600 unless its manual says otherwise.</param>
/// <param name="Parity"><c>none</c>, <c>even</c> or <c>odd</c>.</param>
/// <param name="StopBits"><c>one</c> or <c>two</c>.</param>
internal sealed record WorkstationScanner(
    string? Port = null,
    int BaudRate = 9600,
    int DataBits = 8,
    string Parity = "none",
    string StopBits = "one",
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null);

/// <summary>
/// What <c>scanner.getSetup</c> answers: the scanner HC360 listens to, whether IT fixed it in the machine policy, why a
/// scanner that was set is not used, whether HC360 is listening to it now, and the COM ports this computer has.
/// </summary>
internal sealed record ScannerSetupInfo(
    WorkstationScanner Setup,
    bool LockedByPolicy,
    string ComputerName,
    IReadOnlyList<string> Problems,
    ScannerStatus Status,
    IReadOnlyList<string> Ports);

/// <summary>
/// The workstation's serial barcode scanner, kept for the computer like its printers (<see cref="PrinterSetupStore"/>,
/// whose notes on who may change it apply here too): <c>%ProgramData%\HyFib\HC360Desktop\Workstation\scanner.json</c>, or
/// a <c>scanner</c> entry in the machine policy, which IT may set to fix it.
/// </summary>
internal sealed partial class ScannerSetupStore(string folder, string? policyPath)
{
    public static readonly int[] BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath => Path.Combine(folder, "scanner.json");

    public static ScannerSetupStore ForThisComputer() => new(WorkstationFiles.Folder, WorkstationFiles.PolicyPath);

    /// <summary>
    /// The policy's scanner when IT set one, else the saved one; no scanner when neither can be read or the scanner is
    /// not a valid setup (with the reason in the problems).
    /// </summary>
    public (WorkstationScanner Setup, bool LockedByPolicy, IReadOnlyList<string> Problems) Load()
    {
        if (ReadPolicy() is { } fixedByIt)
        {
            var (usable, problems) = Usable(fixedByIt, "machine policy");
            return (usable, true, problems);
        }

        try
        {
            var saved = File.Exists(FilePath) ? JsonSerializer.Deserialize<WorkstationScanner>(File.ReadAllText(FilePath), Json) : null;
            var (usable, problems) = Usable(saved ?? new WorkstationScanner(), FilePath);
            return (usable, false, problems);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "The barcode scanner of this computer could not be read from {Path}", FilePath);
            return (new WorkstationScanner(), false, ["The saved scanner could not be read. Set the scanner again."]);
        }
    }

    private static (WorkstationScanner Usable, IReadOnlyList<string> Problems) Usable(WorkstationScanner setup, string source)
    {
        try
        {
            Validate(setup);
            return (setup, []);
        }
        catch (BridgeException ex)
        {
            Log.Warning("Scanner setup from {Source}: {Problem}", source, ex.Message);
            return (setup with { Port = null }, [$"The scanner is not used: {ex.Message}"]);
        }
    }

    /// <summary>Checks and saves the scanner for everyone on this computer, and returns what was saved.</summary>
    /// <exception cref="BridgeException">IT fixed the scanner, the setup is not valid, or Windows refused the write.</exception>
    public WorkstationScanner Save(WorkstationScanner setup, DateTimeOffset now)
    {
        if (ReadPolicy() is not null)
        {
            throw new BridgeException("not_allowed", "IT set the barcode scanner of this computer in the HC360 Desktop policy. Ask IT to change it.");
        }

        Validate(setup);

        // As for the printers: UpdatedBy is the name the server approved the change for, and the server's audit trail is
        // the record of who changed the scanner.
        var saved = setup with { Port = setup.Port?.ToUpperInvariant(), UpdatedAt = now, UpdatedBy = WorkstationFiles.Clean(setup.UpdatedBy, 128) };
        try
        {
            WorkstationFiles.Write(folder, FilePath, JsonSerializer.Serialize(saved, Json));
            Log.Information("Barcode scanner of this computer saved by {User}: {Port}", saved.UpdatedBy ?? "(unknown)", saved.Port ?? "none");
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "The barcode scanner of this computer could not be saved to {Path}", FilePath);
            throw new BridgeException(
                "not_saved",
                "Windows did not let HC360 save the scanner for this computer. Ask IT to run Install-HC360Desktop.ps1 again: it sets up the folder for every user.");
        }
    }

    /// <summary>The rules the staff app's form follows too; the first problem is reported in words staff understand.</summary>
    internal static void Validate(WorkstationScanner setup)
    {
        if (setup.Port is null)
        {
            return;
        }

        if (!IsComPort(setup.Port))
        {
            throw Invalid("Choose the scanner's COM port (COM1 to COM256).");
        }

        if (!BaudRates.Contains(setup.BaudRate))
        {
            throw Invalid("Choose the scanner's speed: 1200 to 115200 baud, as set on the scanner.");
        }

        if (setup.DataBits is not (7 or 8))
        {
            throw Invalid("Use 7 or 8 data bits, as set on the scanner.");
        }

        if (setup.Parity is not ("none" or "even" or "odd"))
        {
            throw Invalid("Choose no parity, even or odd, as set on the scanner.");
        }

        if (setup.StopBits is not ("one" or "two"))
        {
            throw Invalid("Use 1 or 2 stop bits, as set on the scanner.");
        }
    }

    /// <summary>COM1 to COM256, the ports Windows names serial lines with.</summary>
    internal static bool IsComPort(string? name) => name is not null && ComPortPattern().IsMatch(name);

    private static BridgeException Invalid(string message) => new("bad_request", message);

    private WorkstationScanner? ReadPolicy() => WorkstationFiles.ReadPolicy<WorkstationScanner>(policyPath, "scanner", Json);

    [GeneratedRegex("^COM([1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-6])$", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortPattern();
}
