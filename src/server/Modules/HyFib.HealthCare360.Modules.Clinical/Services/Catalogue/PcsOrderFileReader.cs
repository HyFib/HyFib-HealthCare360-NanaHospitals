using System.Text;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

/// <summary>An ICD-10-PCS code from the order file: its long title, and the table it belongs to as its category.</summary>
public sealed record PcsCode(string Code, string Title, string Category);

public sealed record PcsOrderFile(string? Version, IReadOnlyList<PcsCode> Codes, int Tables, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads the CMS ICD-10-PCS order file (<c>icd10pcs_order_2025.txt</c>), which is in the public domain. Each line is
/// fixed-width: the order number (5 digits), a space, the code (7 characters, left-aligned), a space, 1 for a valid
/// code or 0 for a table heading, a space, the short title (60 characters), a space and the long title. Valid codes
/// are imported with their long title; a table heading ("Upper Arteries, Bypass") becomes the category of the codes in
/// its table.
/// </summary>
public static partial class PcsOrderFileReader
{
    private const int MaxTitleLength = 300;
    private const int MaxCategoryLength = 64;

    public static PcsOrderFile Read(Stream text, string? fileName, CatalogueFileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(limits);
        using var reader = new StreamReader(text, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        var codes = new List<(string Code, string Title)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var invalid = 0;
        var cut = 0;
        var buffer = new StringBuilder();
        var lineNumber = 0;
        while (ReadLine(reader, buffer, limits.MaxPcsLineLength, lineNumber + 1) is { } line)
        {
            lineNumber++;
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var match = OrderLine().Match(line);
            if (!match.Success)
            {
                if (lineNumber == 1)
                {
                    throw new CatalogueFileException($"This is not the ICD-10-PCS order file: its first line is not an order line. {CatalogueFile.AcceptedFiles}");
                }

                if (++invalid <= 5)
                {
                    warnings.Add($"Line {lineNumber} is not an order line and was skipped.");
                }

                continue;
            }

            var code = match.Groups["code"].Value;
            var shortTitle = match.Groups["short"].Value.Trim();
            var longTitle = match.Groups["long"].Success && match.Groups["long"].Value.Trim().Length > 0 ? match.Groups["long"].Value.Trim() : shortTitle;
            if (match.Groups["valid"].Value == "0")
            {
                tables.TryAdd(code, longTitle);
                continue;
            }

            if (code.Length != 7)
            {
                if (++invalid <= 5)
                {
                    warnings.Add($"Line {lineNumber}: {code} is marked valid but is not seven characters; it was skipped.");
                }

                continue;
            }

            if (!seen.Add(code))
            {
                continue;
            }

            if (codes.Count >= limits.MaxCodes)
            {
                throw new CatalogueFileException($"The file has more than {limits.MaxCodes:N0} codes, more than any ICD-10-PCS edition.");
            }

            var title = CatalogueText.Clean(longTitle);
            if (title.Length > MaxTitleLength)
            {
                cut++;
            }

            codes.Add((code, CatalogueText.Fit(title, MaxTitleLength)));
        }

        if (invalid > 5)
        {
            warnings.Add($"…and {invalid - 5} more lines like these.");
        }

        if (invalid > Math.Max(10, codes.Count / 100))
        {
            throw new CatalogueFileException($"{invalid} lines of the file are not order lines. Is it the ICD-10-PCS order file (icd10pcs_order_YYYY.txt)?");
        }

        if (codes.Count < limits.MinPcsCodes)
        {
            throw new CatalogueFileException(
                $"The file has only {codes.Count:N0} valid {(codes.Count == 1 ? "code" : "codes")}; the CMS order file has about 78,000. Check that it is the complete order file.");
        }

        if (cut > 0)
        {
            warnings.Add($"{cut} titles longer than {MaxTitleLength} characters were shortened.");
        }

        var result = codes.Select(c => new PcsCode(c.Code, c.Title, CategoryOf(c.Code, tables))).ToList();
        return new PcsOrderFile(VersionOf(fileName), result, tables.Count, warnings);
    }

    /// <summary>The code's table heading ("Upper Arteries, Bypass"), else its section ("Medical and Surgical").</summary>
    internal static string CategoryOf(string code, IReadOnlyDictionary<string, string> tables) =>
        CatalogueText.Fit(tables.TryGetValue(code[..3], out var table) ? CatalogueText.Clean(table) : SectionOf(code[0]), MaxCategoryLength);

    /// <summary>The 17 sections of ICD-10-PCS, by the code's first character.</summary>
    internal static string SectionOf(char section) => section switch
    {
        '0' => "Medical and Surgical",
        '1' => "Obstetrics",
        '2' => "Placement",
        '3' => "Administration",
        '4' => "Measurement and Monitoring",
        '5' => "Extracorporeal or Systemic Assistance and Performance",
        '6' => "Extracorporeal or Systemic Therapies",
        '7' => "Osteopathic",
        '8' => "Other Procedures",
        '9' => "Chiropractic",
        'B' => "Imaging",
        'C' => "Nuclear Medicine",
        'D' => "Radiation Therapy",
        'F' => "Physical Rehabilitation and Diagnostic Audiology",
        'G' => "Mental Health",
        'H' => "Substance Abuse Treatment",
        'X' => "New Technology",
        _ => "Other",
    };

    /// <summary>The fiscal year in the CMS file name (icd10pcs_order_2025.txt → 2025).</summary>
    internal static string? VersionOf(string? fileName) =>
        fileName is not null && FileYear().Match(fileName) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>One line, refused when it is longer than any order line can be (so a file without line breaks is not read whole).</summary>
    private static string? ReadLine(StreamReader reader, StringBuilder buffer, int maxLength, int lineNumber)
    {
        buffer.Clear();
        int next;
        while ((next = reader.Read()) >= 0)
        {
            if (next == '\n')
            {
                return buffer.ToString().TrimEnd('\r');
            }

            if (buffer.Length >= maxLength)
            {
                throw new CatalogueFileException($"Line {lineNumber} is longer than {maxLength} characters, so this is not the ICD-10-PCS order file.");
            }

            buffer.Append((char)next);
        }

        return buffer.Length > 0 ? buffer.ToString().TrimEnd('\r') : null;
    }

    /// <summary>"00002 0016070 1 Bypass Cereb Vent to Nasophar with Autol Sub, Open Approach  Bypass Cerebral Ventricle…".</summary>
    [GeneratedRegex(@"^(?<order>[0-9]{5}) (?<code>[0-9A-HJ-NP-Z]{1,7}) *(?<valid>[01]) (?<short>.{0,60})(?: (?<long>.*))?$")]
    private static partial Regex OrderLine();

    [GeneratedRegex(@"icd10pcs_order_(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex FileYear();
}
