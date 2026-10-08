using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// Reads the cell values of an Excel workbook (.xlsx), sheet by sheet, as text: shared and inline strings, numbers
/// (dates stay Excel serial numbers, which <see cref="PosSettlementFile"/> reads as dates) and booleans. Formatting,
/// formulas and everything else are ignored. DTDs are refused, and a workbook that would unpack to more than
/// <paramref name="maxUnzippedBytes"/> or has more than <paramref name="maxRows"/> rows in a sheet is not read.
/// </summary>
internal static class XlsxSheets
{
    private const int MaxColumns = 200;
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,

        // A settlement report's sheet holds a few million characters; a part far larger is not a report.
        MaxCharactersInDocument = 64_000_000,
    };

    public static Result<IReadOnlyList<IReadOnlyList<string[]>>> Read(byte[] content, long maxUnzippedBytes, int maxRows)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            if (zip.Entries.Count > 2_000 || zip.Entries.Sum(e => e.Length) > maxUnzippedBytes)
            {
                return Error.Field("file", "The workbook is too large to read. Save the report as CSV and import that.");
            }

            var strings = SharedStrings(zip);
            var sheets = new List<IReadOnlyList<string[]>>();
            foreach (var path in SheetPaths(zip))
            {
                if (zip.GetEntry(path) is not { } entry)
                {
                    continue;
                }

                var rows = ReadSheet(entry, strings, maxRows);
                if (rows is null)
                {
                    return Error.Field("file", "The report has too many rows. Import it in parts, for example a week at a time.");
                }

                sheets.Add(rows);
            }

            if (sheets.Count == 0)
            {
                return Error.Field("file", "The workbook has no sheets with data.");
            }

            return Result<IReadOnlyList<IReadOnlyList<string[]>>>.Ok(sheets);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or FormatException or OverflowException or NotSupportedException)
        {
            return Error.Field("file", "The workbook could not be read. Open it in Excel and save it as .xlsx or .csv, then import that.");
        }
    }

    /// <summary>The worksheets in the workbook's order, from xl/workbook.xml and its relationships.</summary>
    private static IReadOnlyList<string> SheetPaths(ZipArchive zip)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            using var reader = XmlReader.Create(rels.Open(), Settings);
            while (reader.Read())
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "Relationship", NamespaceURI: PackageRelationshipsNs }
                    && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                {
                    targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                }
            }
        }

        var paths = new List<string>();
        if (zip.GetEntry("xl/workbook.xml") is { } workbook)
        {
            using var reader = XmlReader.Create(workbook.Open(), Settings);
            while (reader.Read())
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "sheet", NamespaceURI: Main }
                    && reader.GetAttribute("id", RelationshipsNs) is { } id && targets.TryGetValue(id, out var path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths.Count > 0
            ? paths
            : zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not { } entry)
        {
            return strings;
        }

        using var reader = XmlReader.Create(entry.Open(), Settings);
        var text = new StringBuilder();
        var inPhonetic = 0;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NamespaceURI == Main)
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "t", IsEmptyElement: false } && inPhonetic == 0)
                {
                    // Reading the content moves to the node after </t>, which must not be skipped.
                    text.Append(reader.ReadElementContentAsString());
                    continue;
                }

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.LocalName == "si":
                        text.Clear();
                        if (reader.IsEmptyElement)
                        {
                            strings.Add(string.Empty);
                        }

                        break;
                    case XmlNodeType.Element when reader.LocalName == "rPh" && !reader.IsEmptyElement:
                        inPhonetic++;
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == "rPh":
                        inPhonetic--;
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == "si":
                        strings.Add(text.ToString());
                        break;
                }
            }

            reader.Read();
        }

        return strings;
    }

    /// <summary>The sheet's rows from the first, each with its cells by column; null when it has more than <paramref name="maxRows"/> rows.</summary>
    private static List<string[]>? ReadSheet(ZipArchiveEntry entry, List<string> strings, int maxRows)
    {
        var rows = new List<string[]>();
        using var reader = XmlReader.Create(entry.Open(), Settings);
        List<string>? row = null;
        string? type = null;
        var column = 0;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader is { NodeType: XmlNodeType.Element, NamespaceURI: Main, IsEmptyElement: false } && row is not null && column < MaxColumns
                && (reader.LocalName == "v" || (reader.LocalName == "t" && type == "inlineStr")))
            {
                // Reading the content moves to the node after the value, which must not be skipped.
                var inline = reader.LocalName == "t";
                var raw = reader.ReadElementContentAsString();
                Put(row, column, inline ? raw : Value(raw, type, strings));
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == Main)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        var number = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : rows.Count + 1;
                        if (number > maxRows + 1000)
                        {
                            return null;
                        }

                        while (rows.Count < number - 1)
                        {
                            rows.Add([]);
                        }

                        row = [];
                        if (reader.IsEmptyElement)
                        {
                            rows.Add([]);
                            row = null;
                        }

                        break;
                    case "c" when row is not null:
                        type = reader.GetAttribute("t");
                        column = ColumnIndex(reader.GetAttribute("r")) ?? row.Count;
                        if (reader.IsEmptyElement)
                        {
                            type = null;
                        }

                        break;
                }
            }
            else if (reader is { NodeType: XmlNodeType.EndElement, LocalName: "row" } && row is not null)
            {
                rows.Add(row.ToArray());
                row = null;
            }

            reader.Read();
        }

        return rows;
    }

    private static void Put(List<string> row, int column, string value)
    {
        while (row.Count <= column)
        {
            row.Add(string.Empty);
        }

        row[column] = value;
    }

    private static string Value(string raw, string? type, List<string> strings) => type switch
    {
        "s" => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < strings.Count ? strings[index] : string.Empty,
        "b" => raw == "1" ? "TRUE" : "FALSE",
        "str" or "inlineStr" or "e" => raw,

        // Numbers as Excel writes them, without exponents: an RRN typed as a number reads as its digits.
        _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.##########", CultureInfo.InvariantCulture)
            : raw,
    };

    /// <summary>The zero-based column of a cell reference such as "AB12".</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var index = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                index = (index * 26) + (c - 'A' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }

        return letters is 0 or > 3 ? null : index - 1;
    }
}
