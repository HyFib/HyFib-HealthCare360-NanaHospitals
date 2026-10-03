using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// The printer for one kind of print. For labels and wristbands also how HC360 talks to it: <c>zpl</c> or <c>tspl</c>
/// (printer commands made by the server, sent raw) or <c>driver</c> (the page printed through the Windows driver), and for
/// the first two the resolution and the size of one label (or one band: width across it, length along it).
/// </summary>
/// <param name="Code">Wristbands only: <c>code128</c> (line scanners) or <c>qr</c> (2D scanners).</param>
internal sealed record RolePrinter(
    string Printer,
    string? Language = null,
    int? Dpi = null,
    decimal? WidthMm = null,
    decimal? HeightMm = null,
    string? Code = null);

/// <summary>Which printer this computer uses for each kind of print. A kind with no printer shows the print dialog.</summary>
internal sealed record WorkstationPrinters(
    RolePrinter? Labels = null,
    RolePrinter? Wristbands = null,
    RolePrinter? Receipts = null,
    RolePrinter? Documents = null,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null);

/// <summary>
/// What <c>printer.getSetup</c> answers: the printers HC360 uses, whether IT fixed them in the machine policy, and why
/// any printer that was set is not used (not installed any more, or not a valid setup): those kinds of print fall back
/// to the page and the print dialog until someone sets them again.
/// </summary>
internal sealed record PrinterSetupInfo(
    WorkstationPrinters Setup,
    bool LockedByPolicy,
    string ComputerName,
    IReadOnlyList<string> Problems)
{
    public PrinterSetupInfo(WorkstationPrinters setup, bool lockedByPolicy, string computerName)
        : this(setup, lockedByPolicy, computerName, [])
    {
    }
}

/// <summary>
/// The workstation's printers, kept for the computer rather than the Windows account, so they are the same for everyone
/// who uses HC360 on it (shared logons and personal logons alike): <c>%ProgramData%\HyFib\HC360Desktop\Workstation\printers.json</c>.
/// The folder lets every Windows user on the computer change the file, because HC360 Desktop saves it for whoever is
/// signed in to Windows. The staff app offers the change only to staff with the "set up printers" permission, after
/// the server approved it, and the server records each change; HC360 Desktop cannot check either itself (it holds no
/// user's sign-in and nothing the server signs), so it saves what the staff app sends, and anyone signed in to Windows
/// here could edit the file. Where that matters, IT fixes the printers with a <c>printers</c> entry in the read-only
/// machine policy (<c>policy.json</c>); then HC360 shows them and refuses changes. Whatever is read, saved file or
/// policy, is checked as a save is: a printer that is not valid or not installed is not used.
/// </summary>
internal sealed class PrinterSetupStore(string folder, string? policyPath)
{
    public const int MaxNameLength = 260;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] LabelLanguages = ["zpl", "tspl", "driver"];

    // Label rolls and wristband bands: width from, to; length from, to (mm).
    private static readonly (decimal MinWidth, decimal MaxWidth, decimal MinHeight, decimal MaxHeight) LabelSizes =
        (25m, 110m, 15m, 100m);

    private static readonly (decimal MinWidth, decimal MaxWidth, decimal MinHeight, decimal MaxHeight) BandSizes =
        (15m, 40m, 80m, 350m);

    public string FilePath => Path.Combine(folder, "printers.json");

    public static PrinterSetupStore ForThisComputer()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyFib", "HC360Desktop");
        return new PrinterSetupStore(Path.Combine(root, "Workstation"), Path.Combine(root, "policy.json"));
    }

    /// <summary>
    /// The policy's printers when IT set them, else the saved ones; nothing set when neither can be read. A kind of
    /// print whose printer is not valid or not installed is left unset, with the reason in the problems.
    /// </summary>
    /// <param name="installed">The printers installed on this computer.</param>
    public PrinterSetupInfo Load(IReadOnlyCollection<string> installed)
    {
        if (ReadPolicy() is { } fixedByIt)
        {
            var (usable, problems) = Usable(fixedByIt, installed);
            LogProblems(problems, "machine policy");
            return new PrinterSetupInfo(usable, true, Environment.MachineName, problems);
        }

        try
        {
            var saved = File.Exists(FilePath) ? JsonSerializer.Deserialize<WorkstationPrinters>(File.ReadAllText(FilePath), Json) : null;
            var (usable, problems) = Usable(saved ?? new WorkstationPrinters(), installed);
            LogProblems(problems, FilePath);
            return new PrinterSetupInfo(usable, false, Environment.MachineName, problems);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable: print with the dialog until someone sets the printers again.
            Log.Warning(ex, "The printers of this computer could not be read from {Path}", FilePath);
            return new PrinterSetupInfo(
                new WorkstationPrinters(),
                false,
                Environment.MachineName,
                ["The saved printers could not be read. Set the printers again."]);
        }
    }

    /// <summary>
    /// The setup with every kind of print whose printer breaks the rules of <see cref="Validate"/> left unset, and why
    /// each was left out (in words staff understand). A wristband code other than Code 128 or QR leaves the wristband
    /// printer unset too, rather than guessing the code.
    /// </summary>
    internal static (WorkstationPrinters Usable, IReadOnlyList<string> Problems) Usable(
        WorkstationPrinters setup,
        IReadOnlyCollection<string> installed)
    {
        var problems = new List<string>();
        RolePrinter? Keep(
            RolePrinter? role, string what, (decimal, decimal, decimal, decimal)? stockSizes, bool band = false)
        {
            try
            {
                CheckRole(role, what, installed, stockSizes);
                if (band)
                {
                    CheckCode(role);
                }

                return role;
            }
            catch (BridgeException ex)
            {
                problems.Add($"The {what} is not used: {ex.Message}");
                return null;
            }
        }

        var usable = setup with
        {
            Labels = Keep(setup.Labels, "label printer", LabelSizes),
            Wristbands = Keep(setup.Wristbands, "wristband printer", BandSizes, band: true),
            Receipts = Keep(setup.Receipts, "receipt printer", null),
            Documents = Keep(setup.Documents, "document printer", null),
        };
        return (usable, problems);
    }

    private static void LogProblems(IReadOnlyList<string> problems, string source)
    {
        foreach (var problem in problems)
        {
            Log.Warning("Printer setup from {Source}: {Problem}", source, problem);
        }
    }

    /// <summary>Checks and saves the printers for everyone on this computer, and returns what was saved.</summary>
    /// <param name="installed">The printers installed on this computer.</param>
    /// <exception cref="BridgeException">IT fixed the printers, the setup is not valid, or Windows refused the write.</exception>
    public WorkstationPrinters Save(WorkstationPrinters setup, IReadOnlyCollection<string> installed, DateTimeOffset now)
    {
        if (ReadPolicy() is not null)
        {
            throw new BridgeException("not_allowed", "IT set the printers of this computer in the HC360 Desktop policy. Ask IT to change them.");
        }

        Validate(setup, installed);

        // UpdatedBy is the name the server gave the staff app when it approved the change; HC360 Desktop cannot check
        // it, so it is shown as a note, and the server's audit trail is the record of who changed the printers.
        var saved = setup with { UpdatedAt = now, UpdatedBy = Clean(setup.UpdatedBy, 128) };
        try
        {
            EnsureFolder();
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
            File.Move(temp, FilePath, overwrite: true);
            Log.Information("Printers of this computer saved by {User}", saved.UpdatedBy ?? "(unknown)");
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "The printers of this computer could not be saved to {Path}", FilePath);
            throw new BridgeException(
                "not_saved",
                "Windows did not let HC360 save the printers for this computer. Ask IT to run Install-HC360Desktop.ps1 again: it sets up the folder for every user.");
        }
    }

    /// <summary>The rules the staff app's form follows too; the first problem is reported in words staff understand.</summary>
    internal static void Validate(WorkstationPrinters setup, IReadOnlyCollection<string> installed)
    {
        CheckRole(setup.Labels, "label printer", installed, LabelSizes);
        CheckRole(setup.Wristbands, "wristband printer", installed, BandSizes);
        CheckRole(setup.Receipts, "receipt printer", installed, stockSizes: null);
        CheckRole(setup.Documents, "document printer", installed, stockSizes: null);
        CheckCode(setup.Wristbands);
    }

    private static void CheckCode(RolePrinter? wristbands)
    {
        if (wristbands?.Code is { } code && code is not ("code128" or "qr"))
        {
            throw Invalid("Choose Code 128 or QR code for wristbands.");
        }
    }

    private static void CheckRole(
        RolePrinter? role,
        string what,
        IReadOnlyCollection<string> installed,
        (decimal MinWidth, decimal MaxWidth, decimal MinHeight, decimal MaxHeight)? stockSizes)
    {
        if (role is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(role.Printer) || role.Printer.Length > MaxNameLength || role.Printer.Any(char.IsControl))
        {
            throw Invalid($"Choose the {what}.");
        }

        if (!installed.Contains(role.Printer, StringComparer.OrdinalIgnoreCase))
        {
            throw new BridgeException("printer_not_found", $"The printer '{role.Printer}' is not installed on this computer.");
        }

        if (stockSizes is not { } sizes)
        {
            return;
        }

        if (role.Language is null || !LabelLanguages.Contains(role.Language))
        {
            throw Invalid($"Choose how HC360 prints to the {what}: ZPL, TSPL or the Windows driver.");
        }

        if (role.Language == "driver")
        {
            return;
        }

        if (role.Dpi is not (203 or 300))
        {
            throw Invalid($"Choose the resolution of the {what}: 203 or 300 dpi.");
        }

        if (!Within(role.WidthMm, sizes.MinWidth, sizes.MaxWidth) || !Within(role.HeightMm, sizes.MinHeight, sizes.MaxHeight))
        {
            throw Invalid($"The {what}'s labels must be {sizes.MinWidth:0} to {sizes.MaxWidth:0} mm wide and {sizes.MinHeight:0} to {sizes.MaxHeight:0} mm long.");
        }
    }

    private static bool Within(decimal? value, decimal min, decimal max) =>
        value is { } v && v >= min && v <= max && decimal.Round(v, 1) == v;

    private static BridgeException Invalid(string message) => new("bad_request", message);

    private static string? Clean(string? value, int max)
    {
        var text = value is null ? null : new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    /// <summary>
    /// Creates the folder so every Windows user on the computer may change the files in it (Users: modify, inherited).
    /// The installer creates it the same way; a folder that already exists is left as it is.
    /// </summary>
    private void EnsureFolder()
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

    /// <summary>The <c>printers</c> entry of the machine policy, when IT set one.</summary>
    private WorkstationPrinters? ReadPolicy()
    {
        if (policyPath is null || !File.Exists(policyPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("printers", out var printers)
                   && printers.ValueKind == JsonValueKind.Object
                ? printers.Deserialize<WorkstationPrinters>(Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "The printers in the machine policy could not be read");
            return null;
        }
    }
}
