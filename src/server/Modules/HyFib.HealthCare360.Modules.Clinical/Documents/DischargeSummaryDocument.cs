using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The A4 discharge summary (EHR-01, PP-02): clinical letterhead with the facility logo and the consultant's block, the
/// signing (and amendment) line, the patient strip with the admission and discharge dates, the doctor's sections from
/// diagnosis to advice, follow-up, and the consultant's signature. A draft says so at the top and at the foot of every
/// page. The same document is printed on the ward, downloaded by staff and from the portal.
/// </summary>
internal static class DischargeSummaryDocument
{
    private const string Styles = """
        h2.summary-title { font-size: 14pt; margin: 0 0 4px; }

        """;

    private const string DraftWarning = "DRAFT — not signed";

    /// <summary>
    /// The document as the facility's discharge summaries template says (HMS-04), with the facility logo and, once signed,
    /// the consultant's signature image.
    /// </summary>
    public static async Task<PrintableDocument> BuildAsync(DischargeSummaryPrint print, DocumentAssets assets, CancellationToken cancellationToken)
    {
        var template = await assets.TemplateAsync(PrintTemplateCatalog.ClinicalNote, print.Letterhead?.FacilityId, cancellationToken);
        var logo = template.ShowLogo ? await assets.ImageAsync(print.Letterhead?.LogoFileId, cancellationToken) : null;
        var signature = IsDraft(print) ? null : await assets.ImageAsync(print.Consultant?.SignatureFileId, cancellationToken);
        return Build(print, logo, signature, template);
    }

    public static PrintableDocument Build(DischargeSummaryPrint print, ImageData? logo, ImageData? signature, PrintTemplate? template = null) => new()
    {
        Title = $"Discharge summary {print.Patient.Uhid}",
        FileName = FileName(print),
        Footer = Footer(print),
        Styles = ClinicalDocumentParts.Styles + Styles,
        Template = template,
        Body = Body(print, logo, signature, template),
    };

    /// <summary>"discharge-summary-NH-000123-2026-09-25": the UHID and the discharge date (else the signing or admission date).</summary>
    public static string FileName(DischargeSummaryPrint print)
    {
        var s = print.Summary;
        return (s.DischargedAt ?? s.SignedAt ?? s.AdmittedAt) is { } date
            ? $"discharge-summary-{print.Patient.Uhid}-{DocumentFormat.ToIst(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : $"discharge-summary-{print.Patient.Uhid}";
    }

    /// <summary>"Asha Rao · UHID NH-000123 · Discharge summary · Discharged 25 Sep 2026", and the warning on a draft.</summary>
    public static string Footer(DischargeSummaryPrint print) =>
        string.Join(" · ", new[]
        {
            print.Patient.FullName,
            $"UHID {print.Patient.Uhid}",
            "Discharge summary",
            print.Summary.DischargedAt is { } discharged ? $"Discharged {DocumentFormat.Date(discharged)}" : null,
            IsDraft(print) ? DraftWarning : null,
        }.Where(part => part is not null));

    public static Html Body(DischargeSummaryPrint print, ImageData? logo, ImageData? signature, PrintTemplate? template = null)
    {
        var s = print.Summary;
        var p = print.Patient;
        var amended = s.AmendedAt is { } at ? $" · amended {DocumentFormat.DateTime(at)} ({s.AmendmentReason})" : null;
        var status = IsDraft(print)
            ? Html.Of($"<div class=\"draft\">{DraftWarning}</div>")
            : Html.Of($"<div class=\"small\">Signed {DocumentFormat.DateTime(s.SignedAt)}{amended}</div>");
        var diagnosis = string.Join("\n", new[] { s.Diagnoses, s.DiagnosisCodes.Count > 0 ? $"ICD-10: {string.Join(", ", s.DiagnosisCodes)}" : null }
            .Where(part => !string.IsNullOrEmpty(part)));
        var stay = $"{DocumentFormat.Date(s.AdmittedAt)} → {DocumentFormat.Date(s.DischargedAt)}";
        return Html.Of($"""
            {ClinicalDocumentParts.Letterhead(print.Letterhead, logo, ClinicalDocumentParts.Prescriber(print.Consultant), template)}
            <h2 class="summary-title">Discharge summary</h2>
            {status}
            <div class="patient">
              {ClinicalDocumentParts.Field("Patient", p.FullName)}
              {ClinicalDocumentParts.Field("UHID", p.Uhid)}
              {ClinicalDocumentParts.Field("Age / sex", $"{p.Age ?? DocumentFormat.Missing} / {ClinicalDocumentParts.GenderShort(p.Gender)}")}
              {ClinicalDocumentParts.Field("Admitted / discharged", stay)}
            </div>
            {ClinicalDocumentParts.TextSection("Diagnosis", diagnosis)}
            {ClinicalDocumentParts.TextSection("Presenting complaints", s.PresentingComplaints)}
            {ClinicalDocumentParts.TextSection("Course in hospital", s.CourseInHospital)}
            {ClinicalDocumentParts.TextSection("Procedures", s.Procedures)}
            {ClinicalDocumentParts.TextSection("Investigations", s.InvestigationsSummary)}
            {ClinicalDocumentParts.TextSection("Condition at discharge", s.ConditionAtDischarge)}
            {ClinicalDocumentParts.TextSection("Medicines on discharge", s.Medications)}
            {ClinicalDocumentParts.TextSection("Advice", s.Advice)}
            {ClinicalDocumentParts.FollowUp(s.FollowUpDate, s.FollowUpInstructions)}
            <div class="footer">
              <div class="small">In an emergency, come to the hospital casualty or call the hospital.</div>
              {ClinicalDocumentParts.Signature(Html.Empty, signature, print.Consultant?.DisplayName ?? s.PractitionerName ?? "Consultant")}
            </div>
            """);
    }

    private static bool IsDraft(DischargeSummaryPrint print) => print.Summary.Status == DischargeSummaryStatus.Draft;
}
