using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>What one report puts into the shared A4 frame.</summary>
/// <param name="Title">Printed under the letterhead: "Laboratory report", "Radiology report"…</param>
/// <param name="DocumentTitle">The page title, also the PDF's title: "Lab report NHLAB2600012".</param>
/// <param name="Footer">Printed at the foot of every page with the page numbers: patient, UHID and report number.</param>
/// <param name="VerificationUrl">The portal's public verification page. Without it (no <c>portal.baseUrl</c>) the code is printed without a QR code.</param>
/// <param name="StatusBanner">A warning for a copy that must not be taken for the final report.</param>
/// <param name="Text">The language of the frame's own words (PP-06); English when not given.</param>
internal sealed record ReportFrame(
    string Title,
    string DocumentTitle,
    string FileName,
    string Footer,
    ReportLetterhead Letterhead,
    IReadOnlyList<(string Label, string? Value)> PatientRows,
    Html Body,
    IReadOnlyList<ReportSignatory> Signatories,
    string? VerificationCode,
    string? VerificationUrl,
    string? StatusBanner,
    IReadOnlyList<string> Notes,
    string? Styles,
    DocumentText? Text = null);

/// <summary>
/// The A4 frame shared by lab, radiology and cardiology reports (UX-09, LIS-07): letterhead with the facility logo,
/// patient block, the report body, notes, signatories with their signatures, the QR code that proves the report is
/// genuine, and the end-of-report line, as the facility's reports template says (HMS-04). The same document is printed at
/// the counter, downloaded and sent to the patient: in English at the counter, in the patient's language (its headings
/// and labels; results, names, units and codes as they are) on the portal and in messages.
/// </summary>
internal sealed class ReportDocumentFrame(DocumentAssets assets)
{
    private const string Styles = """
        .report .body h2 { font-size: 11pt; margin: 10px 0 4px; border-bottom: 1px solid #ccc; padding-bottom: 2px; }
        .report .body h3 { font-size: 10pt; margin: 8px 0 2px; }
        .report .body table { margin-bottom: 6px; }
        .report .body th { border-bottom: 1px solid #999; font-weight: 600; font-size: 9pt; color: #333; }
        .report .body td { border-bottom: 1px solid #eee; }
        .report .body p { margin: 0 0 6px; }
        .flag, .abn { font-weight: 700; }
        .crit { font-weight: 800; color: #B10E1C; }
        .notes { margin-top: 8px; font-size: 8.5pt; color: #333; }
        .foot { margin-top: 14px; break-inside: avoid; }

        """;

    public async Task<PrintableDocument> BuildAsync(ReportFrame frame, CancellationToken cancellationToken)
    {
        var text = frame.Text ?? DocumentText.English;
        var letterhead = await LetterheadAsync(frame.Letterhead, cancellationToken);
        var signatories = new List<DocumentSignatory>(frame.Signatories.Count);
        foreach (var signatory in frame.Signatories)
        {
            signatories.Add(new DocumentSignatory(
                signatory.Name, signatory.Qualification, signatory.RegistrationNumber, await assets.ImageAsync(signatory.SignatureFileId, cancellationToken)));
        }

        var verification = frame.VerificationCode is not { } code
            ? Html.Empty
            : frame.VerificationUrl is { } url
                ? DocumentParts.Verification(url, code, text: text)
                : Html.Of($"<div class=\"small\">{text.Format("doc.verify.code", ("code", code))}</div>");
        var notes = frame.Notes.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Html.Of($"<div>{n}</div>")).ToList();

        return new PrintableDocument
        {
            Title = frame.DocumentTitle,
            FileName = frame.FileName,
            Footer = frame.Footer,
            Language = text.Language,
            Styles = DocumentParts.Styles + Styles + frame.Styles,
            Template = letterhead.Template,
            Body = Html.Of($"""
                <div class="report">
                  {DocumentParts.Letterhead(letterhead, text: text)}
                  {DocumentParts.Title(frame.Title)}
                  {DocumentParts.Banner(frame.StatusBanner)}
                  {DocumentParts.Fields(frame.PatientRows)}
                  <main class="body">{frame.Body}</main>
                  {(notes.Count == 0 ? Html.Empty : Html.Of($"<section class=\"notes\">{notes}</section>"))}
                  <footer class="foot">
                    {DocumentParts.Signatories(signatories, text)}
                    {verification}
                    {DocumentParts.End(text: text)}
                  </footer>
                </div>
                """),
        };
    }

    /// <summary>"Asha Rao · UHID NH-000123 · Lab report NHLAB2600012".</summary>
    public static string Footer(ReportPatient patient, string document) => $"{patient.Name} · UHID {patient.Uhid} · {document}";

    /// <summary>The patient's identity rows every report starts with: name, age and sex, UHID.</summary>
    public static IEnumerable<(string Label, string? Value)> IdentityRows(ReportPatient patient, DocumentText text) =>
    [
        (text["doc.patient"], patient.Name),
        (text["doc.age_sex"], $"{patient.Age ?? DocumentFormat.Missing} / {LabReportDocument.GenderShort(patient.Gender)}"),
        ("UHID", patient.Uhid),
    ];

    private async Task<DocumentLetterhead> LetterheadAsync(ReportLetterhead letterhead, CancellationToken cancellationToken)
    {
        var template = await assets.TemplateAsync(PrintTemplateCatalog.Report, letterhead.FacilityId, cancellationToken);
        var logo = template.ShowLogo ? await assets.ImageAsync(letterhead.LogoFileId, cancellationToken) : null;
        return new DocumentLetterhead(letterhead.FacilityName, null, letterhead.Address, letterhead.Phone, letterhead.Email, null, null,
            letterhead.NabhNumber, null, null, letterhead.LetterheadNote, logo)
        {
            Template = template,
        };
    }
}
