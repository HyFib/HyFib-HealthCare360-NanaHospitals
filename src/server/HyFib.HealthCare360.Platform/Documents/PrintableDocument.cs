using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Platform.Documents;

[JsonConverter(typeof(JsonStringEnumConverter<DocumentPageSize>))]
public enum DocumentPageSize
{
    A4 = 0,
    A5,
}

/// <summary>
/// A document laid out for paper: the page (size, margins, and a running footer with page numbers), the house style,
/// and the document's own body and styles. The same HTML is printed by the browser at the counter and turned into the
/// PDF that is downloaded or sent to the patient, so every copy of a document is the same.
/// </summary>
public sealed partial record PrintableDocument
{
    /// <summary>The page title: shown by the print dialog and as the PDF's title.</summary>
    public required string Title { get; init; }

    /// <summary>The download name without extension, for example "lab-report-NHLAB2600012". Made safe on the way in.</summary>
    public required string FileName
    {
        get => _fileName;
        init => _fileName = SafeFileName(value);
    }

    public required Html Body { get; init; }

    public DocumentPageSize PageSize { get; init; } = DocumentPageSize.A4;

    public bool Landscape { get; init; }

    /// <summary>The document's own CSS. It is written in the code, never built from data.</summary>
    public string? Styles { get; init; }

    /// <summary>
    /// Plain text printed at the foot of every page with the page numbers: who and what the page belongs to (for example
    /// the patient, UHID and report number), so a loose page can always be matched to its document.
    /// </summary>
    public string? Footer { get; init; }

    /// <summary>
    /// The document's language (the html lang attribute): "hi" for a document in Hindi, so the browser picks Hindi
    /// hyphenation and line breaking and screen readers a Hindi voice.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// How the hospital prints this kind of document (<see cref="PrintTemplate"/>): on pre-printed stationery the margins
    /// leave its letterhead and footer blank, and the closing note ends the document. The letterhead itself is part of the
    /// body, which leaves it out on pre-printed paper (<see cref="DocumentParts.Letterhead"/>).
    /// </summary>
    public PrintTemplate? Template { get; init; }

    private readonly string _fileName = "document";

    /// <summary>The complete page. Scripts and network access are forbidden by its own content security policy.</summary>
    public string ToHtml()
    {
        var landscape = Landscape ? " landscape" : "";
        var template = Template ?? PrintTemplate.Default;
        var (size, top, side, bottom) = PageSize switch
        {
            DocumentPageSize.A5 => ("A5", 10, 10, 14),
            _ => ("A4", 12, 12, 16),
        };
        var (topSpace, bottomSpace) = template.PrintsLetterhead
            ? (0, 0)
            : (Math.Clamp(template.TopSpaceMm, 0, PrintTemplate.MaxSpaceMm), Math.Clamp(template.BottomSpaceMm, 0, PrintTemplate.MaxSpaceMm));
        var margin = string.Create(CultureInfo.InvariantCulture, $"{top + topSpace}mm {side}mm {bottom + bottomSpace}mm");
        var closing = template.ClosingNote is { Length: > 0 } note ? Html.Of($"<div class=\"closing-note\">{Html.Lines(note)}</div>") : Html.Empty;

        // The running footer keeps to the document's own band, above the stationery's printed footer.
        var footerBox = bottomSpace > 0 ? string.Create(CultureInfo.InvariantCulture, $"padding-bottom: {bottomSpace}mm; ") : "";
        var footer = Footer is { Length: > 0 } text
            ? $"@bottom-left {{ content: {CssString(text)}; {footerBox}{FooterFont} }}"
            : "";

        // Hindi or Tamil text (a translated document, the directions on a prescription, a name typed in its own script)
        // brings its own font, so it prints the same on every computer.
        var body = Body.ToString();
        var fonts = DocumentFonts.For(string.Concat(Title, Footer, body, closing.ToString()));
        var html = new StringBuilder(body.Length + fonts.Length + 4096);
        html.Append("<!doctype html>\n<html lang=\"").Append(Html.Encode(Language)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\">\n")
            .Append("<title>").Append(Html.Encode(Title)).Append("</title>\n<style>\n")
            .Append(fonts)
            .Append(CultureInfo.InvariantCulture, $"@page {{ size: {size}{landscape}; margin: {margin}; {footer} ")
            .Append(CultureInfo.InvariantCulture, $"@bottom-right {{ content: {PageNumbers(Language)}; {footerBox}{FooterFont} }} }}\n")
            .Append(HouseStyle)
            .Append(Styles)
            .Append("\n</style>\n</head>\n<body>\n")
            .Append(body)
            .Append(closing.ToString())
            .Append("\n</body>\n</html>\n");
        return html.ToString();
    }

    public PdfSource ToPdfSource() => new(ToHtml());

    /// <summary>
    /// A CSS string literal. Quotes and backslashes are escaped, and so are angle brackets and control characters, so
    /// the text can neither end the string nor the surrounding style element.
    /// </summary>
    internal static string CssString(string text)
    {
        var css = new StringBuilder(text.Length + 2).Append('"');
        foreach (var ch in text)
        {
            if (ch is '"' or '\\' or '<' or '>' or '&' || char.IsControl(ch))
            {
                css.Append('\\').Append(((int)ch).ToString("X", CultureInfo.InvariantCulture)).Append(' ');
            }
            else
            {
                css.Append(ch);
            }
        }

        return css.Append('"').ToString();
    }

    /// <summary>
    /// "Page 2 of 3" in the document's language, as CSS content: <c>"Page " counter(page) " of " counter(pages)</c>.
    /// </summary>
    internal static string PageNumbers(string language)
    {
        var text = PatientTexts.Catalog.Text(language, "doc.page_of");
        var parts = PagePlaceholder().Split(text)
            .Select((part, index) => index % 2 == 1 ? $"counter({part})" : part.Length == 0 ? null : CssString(part));
        return string.Join(' ', parts.Where(part => part is not null));
    }

    internal static string SafeFileName(string? name)
    {
        var safe = UnsafeFileNameCharacters().Replace((name ?? "").Trim(), "-").Trim('-', '.');
        return safe.Length == 0 ? "document" : safe.Length > 120 ? safe[..120] : safe;
    }

    private const string FooterFont = "font: 7.5pt " + DocumentFonts.Families + ", sans-serif; color: #555;";

    // Matches the staff app's print base (packages/core printDocument) so documents look the same wherever they print.
    private const string HouseStyle = """
        * { box-sizing: border-box; }
        html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        body { margin: 0; font-family: "Segoe UI", "HC360 Devanagari", "HC360 Tamil", "Nirmala UI", system-ui, sans-serif; font-size: 10pt; line-height: 1.35; color: #111; }
        h1, h2, h3 { margin: 0; break-after: avoid; }
        p { margin: 0 0 4px; }
        table { width: 100%; border-collapse: collapse; }
        th, td { text-align: left; vertical-align: top; padding: 3px 4px; }
        thead { display: table-header-group; }
        tr, .keep { break-inside: avoid; }
        .keep-next { break-after: avoid; }
        img { max-width: 100%; }
        .muted { color: #555; }
        .small { font-size: 8.5pt; color: #444; }
        .right { text-align: right; }
        .center { text-align: center; }
        .nowrap { white-space: nowrap; }
        .mono { font-family: Consolas, "Cascadia Mono", monospace; }
        .closing-note { margin-top: 10px; font-size: 8.5pt; color: #333; break-inside: avoid; }

        """;

    [GeneratedRegex(@"[^A-Za-z0-9._-]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnsafeFileNameCharacters();

    // Split keeps the captured name: "Page {page} of {pages}" → "Page ", "page", " of ", "pages", "".
    [GeneratedRegex(@"\{(page|pages)\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PagePlaceholder();
}
