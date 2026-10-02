namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>Someone who signs a document: their name, qualification and council registration (UX-09), and signature image.</summary>
public sealed record DocumentSignatory(string Name, string? Qualification, string? RegistrationNumber, ImageData? Signature);

/// <summary>
/// Parts shared by the hospital's documents, in the house layout of the A4 reports: letterhead, a two-column block of
/// labelled values, signatories and the QR verification block. Documents that use them add <see cref="Styles"/>.
/// </summary>
public static class DocumentParts
{
    /// <summary>The CSS for these parts.</summary>
    public const string Styles = """
        .lh { display: flex; gap: 12px; align-items: center; border-bottom: 2px solid #045E80; padding-bottom: 6px; }
        .lh .logo { max-height: 18mm; max-width: 40mm; object-fit: contain; }
        .lh .facility { font-size: 15pt; font-weight: 700; color: #045E80; }
        .lh .aside { margin-left: auto; text-align: right; }
        .doc-title { text-align: center; font-size: 12pt; letter-spacing: 1px; text-transform: uppercase; margin: 8px 0 6px; }
        .banner { border: 1.5px solid #B10E1C; color: #B10E1C; text-align: center; font-weight: 700; padding: 3px; margin-bottom: 6px; }
        .fields { display: grid; grid-template-columns: 1fr 1fr; gap: 2px 16px; border: 1px solid #999; border-radius: 4px; padding: 6px 8px; margin-bottom: 8px; }
        .fields .cell { display: flex; gap: 6px; }
        .fields .label { color: #555; min-width: 30mm; }
        .fields .value { font-weight: 600; }
        .signs { display: flex; gap: 24px; justify-content: flex-end; flex-wrap: wrap; }
        .sign { text-align: center; min-width: 45mm; }
        .sign .sign-img { max-height: 14mm; max-width: 45mm; }
        .sign .sign-space { height: 14mm; }
        .sign .sign-name { font-weight: 700; border-top: 1px solid #666; padding-top: 2px; }
        .verify { display: flex; gap: 8px; align-items: center; margin-top: 10px; break-inside: avoid; }
        .verify .qr { width: 24mm; height: 24mm; }
        .verify .verify-text { font-size: 8.5pt; }
        .verify .url { word-break: break-all; }
        .end { text-align: center; margin-top: 8px; font-size: 8.5pt; color: #555; }

        """;

    /// <summary>
    /// The letterhead: logo, name, address, "Phone · e-mail", accreditation and the facility's own letterhead line.
    /// <paramref name="aside"/> goes on the right (a doctor's block, say).
    /// </summary>
    public static Html Letterhead(DocumentLetterhead letterhead, Html aside = default)
    {
        ArgumentNullException.ThrowIfNull(letterhead);
        var contact = string.Join(" · ", new[] { letterhead.Phone is { } phone ? $"Phone {phone}" : null, letterhead.Email }.Where(c => c is not null));
        return Html.Of($"""
            <header class="lh">
              {Html.Image(letterhead.Logo, "", "logo")}
              <div>
                <div class="facility">{letterhead.Name}</div>
                {Line(letterhead.Address)}
                {Line(contact)}
                {Line(letterhead.NabhNumber is { } nabh ? $"NABH accreditation {nabh}" : null)}
                {Line(letterhead.Note)}
              </div>
              {(aside.IsEmpty ? Html.Empty : Html.Of($"<div class=\"aside\">{aside}</div>"))}
            </header>
            """);
    }

    /// <summary>The document's title, centred under the letterhead.</summary>
    public static Html Title(string title) => Html.Of($"<h1 class=\"doc-title\">{title}</h1>");

    /// <summary>A warning across the page, for a copy that must not be taken for the final document.</summary>
    public static Html Banner(string? text) => text is null ? Html.Empty : Html.Of($"<div class=\"banner\">{text}</div>");

    /// <summary>Labelled values in two columns (patient, numbers, dates). A missing value prints as a dash.</summary>
    public static Html Fields(IEnumerable<(string Label, string? Value)> fields) =>
        Html.Of($"""<section class="fields">{fields.Select(f => Html.Of($"<div class=\"cell\"><span class=\"label\">{f.Label}</span><span class=\"value\">{f.Value ?? DocumentFormat.Missing}</span></div>"))}</section>""");

    /// <summary>Signature (or space to sign), name, qualification and registration number of each signatory.</summary>
    public static Html Signatories(IEnumerable<DocumentSignatory> signatories)
    {
        var signs = signatories.Select(s => Html.Of($"""
            <div class="sign">
              {(s.Signature is null ? Html.Of($"<div class=\"sign-space\"></div>") : Html.Image(s.Signature, "", "sign-img"))}
              <div class="sign-name">{s.Name}</div>
              {Line(s.Qualification)}
              {Line(s.RegistrationNumber is { } number ? $"Reg. no. {number}" : null)}
            </div>
            """)).ToList();
        return signs.Count == 0 ? Html.Empty : Html.Of($"<section class=\"signs\">{signs}</section>");
    }

    /// <summary>
    /// The QR code with its explanation: scanning it opens the public page that confirms the document is genuine.
    /// </summary>
    public static Html Verification(string url, string code, string caption = "Scan to verify this report") => Html.Of($"""
        <div class="verify">
          {Html.Image(DocumentAssets.QrCode(url), "QR code to verify this document", "qr")}
          <div class="verify-text"><strong>{caption}</strong><div class="muted">Verification code {code}</div><div class="muted url">{url}</div></div>
        </div>
        """);

    /// <summary>The closing line of a report.</summary>
    public static Html End(string text = "— End of report —") => Html.Of($"<div class=\"end\">{text}</div>");

    private static Html Line(string? text) => string.IsNullOrWhiteSpace(text) ? Html.Empty : Html.Of($"<div class=\"muted\">{text}</div>");
}
