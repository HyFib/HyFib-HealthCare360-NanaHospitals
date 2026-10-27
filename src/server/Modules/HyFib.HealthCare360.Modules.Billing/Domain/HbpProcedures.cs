using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// One procedure of AB PM-JAY's Health Benefit Package master (HBP), as the National Health Authority publishes it and
/// the hospital imports it (<see cref="HbpMasterFile"/>): the procedure code (two letters for the specialty, three digits
/// for the package, a letter for each stratified procedure, as <c>BM001A</c>), its package and specialty, the base rate,
/// the expected stay, and whether the scheme reserves it for public hospitals. A procedure that a later master drops is
/// kept, inactive, so packages and claims that name it still read.
/// </summary>
public sealed class HbpProcedure : AuditableEntity
{
    public string Code { get; set; } = string.Empty;

    /// <summary>The package the procedure belongs to: the code's first five characters (<c>BM001</c>).</summary>
    public string PackageCode { get; set; } = string.Empty;

    public string SpecialtyCode { get; set; } = string.Empty;
    public string? Specialty { get; set; }
    public string? PackageName { get; set; }
    public string ProcedureName { get; set; } = string.Empty;

    /// <summary>The scheme's base rate in rupees, before a state's tier and incentive weights.</summary>
    public decimal? Rate { get; set; }

    public int? LengthOfStayDays { get; set; }

    /// <summary>The scheme lets only public hospitals perform it (a private hospital's claim is refused).</summary>
    public bool ReservedForPublic { get; set; }

    /// <summary>What the stratification is by, as the master states it (for example a burn's %TBSA band).</summary>
    public string? Stratification { get; set; }

    /// <summary>The master's name or version as the importer gave it, for example "HBP 2022".</summary>
    public string MasterVersion { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>A procedure read from the master file, before it is saved.</summary>
public sealed record HbpMasterRow(
    string Code,
    string SpecialtyCode,
    string? Specialty,
    string? PackageName,
    string ProcedureName,
    decimal? Rate,
    int? LengthOfStayDays,
    bool ReservedForPublic,
    string? Stratification);

/// <summary>The procedures read, and the rows that could not be, by place and reason.</summary>
public sealed record HbpMasterContent(IReadOnlyList<HbpMasterRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// Reads NHA's HBP master as the hospital receives it (Excel .xlsx, or CSV saved from it), whichever sheet carries the
/// procedure codes. Columns are found by their headings rather than their place, since the state agencies' copies order
/// and name them differently: the procedure code (required), procedure name (required), specialty and its code,
/// package name, rate or price, length of stay, reservation for public hospitals, and stratification. A row whose code
/// is not an HBP procedure code, or that repeats one, is reported and left out; the rest are read.
/// </summary>
public static partial class HbpMasterFile
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxRows = 20_000;

    public const string AcceptedFiles = "Use NHA's HBP master as an Excel workbook (.xlsx) or a CSV file saved from it.";

    private static readonly string[] CodeHeadings = ["procedure code", "hbp 2022 procedure code", "hbp procedure code", "procedure_code", "proc code"];
    private static readonly string[] NameHeadings = ["procedure name", "procedure_name", "procedure"];
    private static readonly string[] SpecialtyHeadings = ["specialty name", "speciality name", "specialty", "speciality"];
    private static readonly string[] SpecialtyCodeHeadings = ["specialty code", "speciality code"];
    private static readonly string[] PackageHeadings = ["package name", "ab pm-jay package name", "package"];
    private static readonly string[] RateHeadings = ["procedure price", "procedure rate", "package rate", "rate", "price", "amount"];
    private static readonly string[] StayHeadings = ["length of stay", "alos", "los"];
    private static readonly string[] ReservedHeadings = ["reserved for public", "reservation", "reserved"];
    private static readonly string[] StratificationHeadings = ["stratification remarks", "stratification", "stratification criteria"];

    public static Result<HbpMasterContent> Read(byte[] content)
    {
        if (content.Length == 0)
        {
            return Error.Field("file", "The file is empty. " + AcceptedFiles);
        }

        if (content.Length > MaxBytes)
        {
            return Error.Field("file", "The file is larger than 10 MB, more than an HBP master is.");
        }

        var sheets = PosSettlementFile.ReadSheets(content);
        if (sheets.IsFailure)
        {
            return sheets.Error!;
        }

        foreach (var sheet in sheets.Value)
        {
            var header = FindHeader(sheet);
            if (header is null)
            {
                continue;
            }

            if (sheet.Rows.Count - header.Value.Row - 1 > MaxRows)
            {
                return Error.Field("file", $"The master has more than {MaxRows:N0} rows, more than an HBP master is.");
            }

            return ReadRows(sheet, header.Value.Row, header.Value.Columns);
        }

        return Error.Field("file", "No sheet has a \"Procedure Code\" and a \"Procedure Name\" column. " + AcceptedFiles);
    }

    /// <summary>An HBP procedure code: two letters, three digits, and the stratification letter when there is one.</summary>
    public static bool IsProcedureCode(string? code) => code is not null && Code().IsMatch(code);

    private static HbpMasterContent ReadRows(ReportSheet sheet, int headerRow, Dictionary<string, int> columns)
    {
        var rows = new List<HbpMasterRow>();
        var problems = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var cells = sheet.Rows[r];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? Cell(string field) => columns.TryGetValue(field, out var index) && index < cells.Length && !string.IsNullOrWhiteSpace(cells[index])
                ? cells[index].Trim()
                : null;

            var place = $"Row {r + 1}";
            var code = Cell("code")?.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            var name = Cell("name");
            if (code is null && name is null)
            {
                continue;
            }

            if (!IsProcedureCode(code))
            {
                problems.Add($"{place}: \"{Shorten(code ?? string.Empty)}\" is not an HBP procedure code (two letters, three digits, then a letter for a stratified procedure).");
                continue;
            }

            if (name is null)
            {
                problems.Add($"{place}: procedure {code} has no name.");
                continue;
            }

            if (seen.TryGetValue(code!, out var first))
            {
                problems.Add($"{place}: procedure {code} is listed again (first on row {first}); the first is kept.");
                continue;
            }

            seen[code!] = r + 1;
            var rateText = Cell("rate");
            decimal? rate = null;
            if (rateText is not null)
            {
                var cleaned = rateText.Replace(",", string.Empty, StringComparison.Ordinal).Replace("₹", string.Empty, StringComparison.Ordinal).Replace("Rs.", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                {
                    rate = decimal.Round(parsed, 2);
                }
                else
                {
                    problems.Add($"{place}: the rate of {code}, \"{Shorten(rateText)}\", is not an amount; it is left blank.");
                }
            }

            int? stay = int.TryParse(Cell("stay")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is > 0 and <= 365
                ? days
                : null;
            rows.Add(new HbpMasterRow(
                code!,
                Cell("specialtyCode")?.ToUpperInvariant() is { Length: 2 } specialtyCode && specialtyCode == code![..2] ? specialtyCode : code![..2],
                Clip(Cell("specialty"), 200),
                Clip(Cell("package"), 300),
                Clip(name, 500)!,
                rate,
                stay,
                IsYes(Cell("reserved")),
                Clip(Cell("stratification"), 500)));
        }

        return new HbpMasterContent(rows, problems);
    }

    private static (int Row, Dictionary<string, int> Columns)? FindHeader(ReportSheet sheet)
    {
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 30); r++)
        {
            var headings = sheet.Rows[r].Select(c => Normalize(c)).ToArray();
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            // The heading named first wins (an exact match before a prefix), so "Stratification Remarks" is read rather
            // than the "Stratification Criteria" flag beside it.
            void Find(string field, string[] names)
            {
                foreach (var exact in new[] { true, false })
                {
                    foreach (var name in names)
                    {
                        for (var c = 0; c < headings.Length; c++)
                        {
                            if (!columns.ContainsValue(c) && (exact ? headings[c] == name : headings[c].StartsWith(name, StringComparison.Ordinal)))
                            {
                                columns[field] = c;
                                return;
                            }
                        }
                    }
                }
            }

            // Codes before names, and specialty codes before specialties, so "Procedure Code" is not read as a name.
            Find("code", CodeHeadings);
            Find("specialtyCode", SpecialtyCodeHeadings);
            Find("name", NameHeadings);
            Find("specialty", SpecialtyHeadings);
            Find("package", PackageHeadings);
            Find("rate", RateHeadings);
            Find("stay", StayHeadings);
            Find("reserved", ReservedHeadings);
            Find("stratification", StratificationHeadings);
            if (columns.ContainsKey("code") && columns.ContainsKey("name"))
            {
                return (r, columns);
            }
        }

        return null;
    }

    private static string Normalize(string? heading) =>
        Whitespace().Replace((heading ?? string.Empty).Replace('_', ' ').Replace('\n', ' ').Trim().ToLowerInvariant(), " ");

    private static bool IsYes(string? value) => value?.Trim().ToLowerInvariant() is "y" or "yes" or "true" or "1" or "reserved" or "public";

    private static string Shorten(string text) => text.Length > 30 ? text[..30] + "…" : text;

    private static string? Clip(string? text, int length) => text is null ? null : text.Length > length ? text[..length] : text;

    [GeneratedRegex(@"^[A-Z]{2}[0-9]{3}[A-Z]?$", RegexOptions.CultureInvariant)]
    private static partial Regex Code();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
