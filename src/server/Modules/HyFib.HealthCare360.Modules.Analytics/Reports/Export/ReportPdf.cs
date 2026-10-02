using System.Globalization;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Export;

// ClosedXML brings the Open XML SDK's global DocumentFormat namespace, which hides the document layer's DocumentFormat;
// an alias inside the namespace is found first.
using DocumentFormat = HyFib.HealthCare360.Platform.Documents.DocumentFormat;

/// <summary>
/// The PDF of a report (MIS-03), laid out as an A4 document in the house style: the facility's letterhead, the title,
/// the inputs in words, who generated the report, when and from which data source, the headline figures, the table
/// (headings repeated on every page, numbers right-aligned in the Excel export's formats with Indian digit grouping),
/// totals, notes, the definition and the confidentiality notice. A table too wide for a portrait page turns the page
/// landscape. Every page ends with the report, the hospital, when it was generated and "Confidential", as every page of
/// the Excel sheet does. <see cref="ReportFileWriter"/> turns it into the PDF.
/// </summary>
public static class ReportPdf
{
    public const string ContentType = "application/pdf";

    /// <summary>
    /// Most rows a PDF lists. A longer report still gets its PDF: the first rows in the order on screen, with a notice
    /// at the top (and in the export's answer) that the Excel export has every row; the totals still cover every row.
    /// It is cut rather than refused because a refusal would leave a scheduled PDF e-mail with nothing to send on the
    /// day its report runs long, and the first rows of a sorted report (the biggest departments, the soonest expiries)
    /// are what a printed copy is read for. 2,000 rows are about 40 to 60 pages: still readable, and small enough for
    /// the PDF renderer to finish well inside its time limit (30 seconds by default). Aggregate reports stop at 5,000
    /// rows anyway (<see cref="ReportRunner.RowCap"/>).
    /// </summary>
    public const int MaxRows = 2_000;

    /// <summary>A table with more columns than this is printed landscape, however narrow its columns.</summary>
    public const int MaxPortraitColumns = 9;

    /// <summary>
    /// Characters a line of the table holds on A4 portrait: 186 mm between the margins, at about 1.55 mm a character in
    /// the table's 8.5 pt type. A wider table is printed landscape (273 mm).
    /// </summary>
    public const int PortraitCharacters = 120;

    // Text wraps between words, so a long text column counts only this much; numbers and dates do not wrap.
    private const int TextWrapsAt = 40;

    // Cell padding and a little air, in characters, for every column.
    private const int ColumnPadding = 2;

    private static readonly NumberFormatInfo IndianNumbers = CultureInfo.GetCultureInfo("en-IN").NumberFormat;

    private const string Styles = """
        .mis h2 { font-size: 10pt; margin: 10px 0 3px; }
        .figures { display: flex; flex-wrap: wrap; gap: 6px; margin: 0 0 8px; }
        .figure { border: 1px solid #C8D3E3; border-radius: 4px; padding: 3px 10px; min-width: 32mm; }
        .figure-value { font-size: 11pt; font-weight: 700; }
        .figure-label { font-size: 8pt; color: #555; }
        .grid { font-size: 8.5pt; }
        .grid th, .grid td { padding: 2px 4px; }
        .grid th { background: #E8EEF7; border-bottom: 1px solid #999; font-weight: 600; vertical-align: bottom; }
        .grid td { border-bottom: 1px solid #E6E6E6; overflow-wrap: anywhere; }
        .grid .num { text-align: right; white-space: nowrap; overflow-wrap: normal; font-variant-numeric: tabular-nums; }
        .grid .neg { color: #A4262C; }
        .grid .totals td { font-weight: 700; border-top: 1.5px solid #333; border-bottom: 0; }
        .empty { margin: 10px 0; font-style: italic; }
        .notes p, .definition p { font-size: 8.5pt; color: #333; }
        .confidential { margin-top: 10px; font-size: 8.5pt; font-weight: 700; color: #A4262C; }

        """;

    /// <summary>The printable report under <paramref name="letterhead"/> (made by <see cref="Letterhead"/>).</summary>
    public static PrintableDocument Build(ReportDocument document, DocumentLetterhead letterhead)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(letterhead);
        var columns = document.Columns;
        var rows = document.Rows.Take(MaxRows).Select(values => Cells(values, columns, isTotal: false)).ToList();
        var totals = document.Totals is { } values ? Cells(values, columns, isTotal: true) : null;
        var notice = Notice(document.Rows.Count);
        var banner = notice is null || document.Totals is null ? notice : notice + " The totals are for all rows.";
        var table = rows.Count == 0 ? Html.Of($"<p class=\"empty\">No data for these inputs.</p>") : Table(columns, rows, totals);

        return new PrintableDocument
        {
            Title = document.Title,
            FileName = ReportExporter.FileStem(document),
            Footer = Footer(document),
            Landscape = IsLandscape(columns, totals is null ? rows : [.. rows, totals]),
            Styles = DocumentParts.Styles + Styles,
            Body = Html.Of($"""
                <div class="mis">
                  {DocumentParts.Letterhead(letterhead)}
                  {DocumentParts.Title(document.Title)}
                  {DocumentParts.Banner(banner)}
                  {DocumentParts.Fields(Header(document))}
                  {Summary(document.Summary)}
                  {table}
                  {Notes(notice, document.Notes)}
                  <section class="definition keep"><h2>Definition</h2><p>{document.Definition}</p></section>
                  <p class="confidential">{ReportExporter.ConfidentialNotice(document)}</p>
                  {DocumentParts.End()}
                </div>
                """),
        };
    }

    /// <summary>
    /// The facility's letterhead for a report, named as in the Excel export (the facility's legal name, or the
    /// organisation for a consolidated report). A consolidated report covers every facility, so its letterhead keeps
    /// only the name and the logo.
    /// </summary>
    public static DocumentLetterhead Letterhead(DocumentLetterhead facility, ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(facility);
        ArgumentNullException.ThrowIfNull(document);
        return document.FacilityId is null
            ? new DocumentLetterhead(document.Organisation, null, null, null, null, null, null, null, null, null, null, facility.Logo)
            : facility with { Name = document.Organisation };
    }

    /// <summary>"Revenue by department · Nana Hospitals · generated 15 Sep 2026, 12:00 pm · Confidential".</summary>
    public static string Footer(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return $"{document.Title} · {document.Organisation} · generated {DocumentFormat.DateTime(document.GeneratedAt)} · {ReportExporter.Confidential}";
    }

    /// <summary>
    /// What the PDF of a report with <paramref name="rows"/> rows leaves out, or null when it lists them all. Printed at
    /// the top of the PDF, sent with the export and shown with the report on screen.
    /// </summary>
    public static string? Notice(int rows) => rows <= MaxRows
        ? null
        : $"The PDF lists the first {DocumentFormat.Number(MaxRows)} of {DocumentFormat.Number(rows)} rows; the Excel export has every row.";

    /// <summary>
    /// A value as printed in a column of <paramref name="type"/>: the Excel export's number formats (#,##0 · ₹ #,##0.00 ·
    /// 0.0% · #,##0.0) with Indian digit grouping, and dates as on the hospital's documents (IST).
    /// </summary>
    public static string? Format(object? value, ReportValueType type) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "Yes" : "No",
        DateOnly date => type == ReportValueType.Month ? date.ToString("MMM yyyy", CultureInfo.InvariantCulture) : DocumentFormat.Date(date),
        DateTimeOffset instant => DocumentFormat.DateTime(instant),
        _ when ReportShaping.ToDecimal(value) is { } number => Number(number, type),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Number(decimal number, ReportValueType type) => type switch
    {
        ReportValueType.Integer => number.ToString("N0", IndianNumbers),
        ReportValueType.Money => DocumentFormat.Money(number),
        ReportValueType.Percent => number.ToString("0.0", CultureInfo.InvariantCulture) + "%",
        _ => number.ToString("N1", IndianNumbers),
    };

    private static bool IsNumber(ReportValueType type) =>
        type is ReportValueType.Integer or ReportValueType.Decimal or ReportValueType.Money or ReportValueType.Percent
            or ReportValueType.Days or ReportValueType.Minutes;

    private static PdfCell[] Cells(object?[] values, IReadOnlyList<ReportColumn> columns, bool isTotal)
    {
        var cells = new PdfCell[columns.Count];
        for (var c = 0; c < cells.Length; c++)
        {
            var value = c < values.Length ? values[c] : null;

            // As in the Excel export, the first cell of the totals row is its label.
            var type = isTotal && c == 0 ? ReportValueType.Text : columns[c].Type;
            string? css = null;
            if (IsNumber(columns[c].Type))
            {
                // Negative money is red, as in Excel; the minus sign is printed too.
                css = type == ReportValueType.Money && ReportShaping.ToDecimal(value) < 0 ? "num neg" : "num";
            }
            else if (columns[c].Type is ReportValueType.Date or ReportValueType.Month)
            {
                css = "nowrap";
            }

            cells[c] = new PdfCell(Format(value, type), css);
        }

        return cells;
    }

    /// <summary>
    /// Landscape when the table would be cramped on a portrait page: more than <see cref="MaxPortraitColumns"/> columns,
    /// or columns wider together than <see cref="PortraitCharacters"/>. A column is as wide as its widest value (text
    /// counted up to <see cref="TextWrapsAt"/> characters, since it wraps) or the longest word of its heading.
    /// </summary>
    private static bool IsLandscape(IReadOnlyList<ReportColumn> columns, IEnumerable<PdfCell[]> rows)
    {
        if (columns.Count > MaxPortraitColumns)
        {
            return true;
        }

        var widths = columns.Select(c => c.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word.Length).DefaultIfEmpty(0).Max()).ToArray();
        foreach (var row in rows)
        {
            for (var c = 0; c < widths.Length; c++)
            {
                var length = row[c].Text?.Length ?? 0;
                widths[c] = Math.Max(widths[c], columns[c].Type == ReportValueType.Text ? Math.Min(length, TextWrapsAt) : length);
            }
        }

        return widths.Sum(width => width + ColumnPadding) > PortraitCharacters;
    }

    private static IEnumerable<(string Label, string? Value)> Header(ReportDocument document) =>
        document.Filters.Select(f => (f.Label, (string?)f.Value))
            .Append(("Generated by", document.GeneratedBy))
            .Append(("Generated on", DocumentFormat.DateTime(document.GeneratedAt)))
            .Append(("Data source", document.DataSource));

    private static Html Summary(IReadOnlyList<ReportFigure> figures) => figures.Count == 0
        ? Html.Empty
        : Html.Of($"""<section class="figures keep">{figures.Select(f => Html.Of($"<div class=\"figure\"><div class=\"figure-value\">{Format(f.Value, f.Type) ?? DocumentFormat.Missing}</div><div class=\"figure-label\">{f.Label}</div></div>"))}</section>""");

    // The totals row is a body of its own after the rows: a table footer would repeat on every page, as page totals.
    private static Html Table(IReadOnlyList<ReportColumn> columns, List<PdfCell[]> rows, PdfCell[]? totals) => Html.Of($"""
        <table class="grid">
          <thead><tr>{columns.Select(c => IsNumber(c.Type) ? Html.Of($"<th class=\"num\">{c.Label}</th>") : Html.Of($"<th>{c.Label}</th>"))}</tr></thead>
          <tbody>{rows.Select(row => Html.Of($"<tr>{row.Select(Cell)}</tr>"))}</tbody>
          {(totals is null ? Html.Empty : Html.Of($"<tbody class=\"totals\"><tr>{totals.Select(Cell)}</tr></tbody>"))}
        </table>
        """);

    private static Html Cell(PdfCell cell) =>
        cell.Css is null ? Html.Of($"<td>{cell.Text}</td>") : Html.Of($"<td class=\"{cell.Css}\">{cell.Text}</td>");

    private static Html Notes(string? notice, IReadOnlyList<string> notes)
    {
        var lines = (notice is null ? notes : [notice, .. notes])
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => Html.Of($"<p>{note}</p>"))
            .ToList();
        return lines.Count == 0 ? Html.Empty : Html.Of($"<section class=\"notes\"><h2>Notes</h2>{lines}</section>");
    }

    /// <summary>A cell as printed, and its class: <c>num</c> (right-aligned), <c>num neg</c>, <c>nowrap</c> or none.</summary>
    private readonly record struct PdfCell(string? Text, string? Css);
}
