using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The A4 discharge summary (EHR-01, PP-02): clinical letterhead with the facility logo and the consultant's block, the
/// signing (and amendment) line, the patient strip with the admission and discharge dates, the doctor's sections from
/// diagnosis to advice, follow-up, and the consultant's signature. A draft says so at the top and at the foot of every
/// page. The same document is printed on the ward, downloaded by staff and from the portal; its headings are in the
/// language it is made in (PP-06: the patient's on the portal and in messages, English on the ward). What the doctor
/// wrote is printed as written.
/// </summary>
internal static class DischargeSummaryDocument
{
    private const string Styles = """
        h2.summary-title { font-size: 14pt; margin: 0 0 4px; }

        """;

    /// <summary>
    /// The document as the facility's discharge summaries template says (HMS-04), with the facility logo and, once signed,
    /// the consultant's signature image.
    /// </summary>
    public static async Task<PrintableDocument> BuildAsync(DischargeSummaryPrint print, DocumentAssets assets, DocumentText text, CancellationToken cancellationToken)
    {
        var template = await assets.TemplateAsync(PrintTemplateCatalog.ClinicalNote, print.Letterhead?.FacilityId, cancellationToken);
        var logo = template.ShowLogo ? await assets.ImageAsync(print.Letterhead?.LogoFileId, cancellationToken) : null;
        var signature = IsDraft(print) ? null : await assets.ImageAsync(print.Consultant?.SignatureFileId, cancellationToken);
        return Build(print, logo, signature, template, text);
    }

    public static PrintableDocument Build(
        DischargeSummaryPrint print, ImageData? logo, ImageData? signature, PrintTemplate? template = null, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        return new PrintableDocument
        {
            Title = text.Format("doc.discharge.document_title", ("uhid", print.Patient.Uhid)),
            FileName = FileName(print),
            Footer = Footer(print, text),
            Language = text.Language,
            Styles = ClinicalDocumentParts.Styles + Styles,
            Template = template,
            Body = Body(print, logo, signature, template, text),
        };
    }

    /// <summary>"discharge-summary-NH-000123-2026-09-25": the UHID and the discharge date (else the signing or admission date).</summary>
    public static string FileName(DischargeSummaryPrint print)
    {
        var s = print.Summary;
        return (s.DischargedAt ?? s.SignedAt ?? s.AdmittedAt) is { } date
            ? $"discharge-summary-{print.Patient.Uhid}-{DocumentFormat.ToIst(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : $"discharge-summary-{print.Patient.Uhid}";
    }

    /// <summary>"Asha Rao · UHID NH-000123 · Discharge summary · Discharged 25 Sep 2026", and the warning on a draft.</summary>
    public static string Footer(DischargeSummaryPrint print, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        return string.Join(" · ", new[]
        {
            print.Patient.FullName,
            $"UHID {print.Patient.Uhid}",
            text["doc.discharge.title"],
            print.Summary.DischargedAt is { } discharged ? text.Format("doc.discharge.discharged_on", ("date", DocumentFormat.Date(discharged))) : null,
            IsDraft(print) ? text["doc.discharge.draft"] : null,
        }.Where(part => part is not null));
    }

    public static Html Body(DischargeSummaryPrint print, ImageData? logo, ImageData? signature, PrintTemplate? template = null, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        var labels = ClinicalLabels.For(text);
        var s = print.Summary;
        var p = print.Patient;
        var amended = s.AmendedAt is { } at
            ? text.Format("doc.discharge.amended", ("at", DocumentFormat.DateTime(at)), ("reason", s.AmendmentReason))
            : null;
        var status = IsDraft(print)
            ? Html.Of($"<div class=\"draft\">{text["doc.discharge.draft"]}</div>")
            : Html.Of($"<div class=\"small\">{text.Format("doc.discharge.signed", ("at", DocumentFormat.DateTime(s.SignedAt)))}{amended}</div>");
        var diagnosis = string.Join("\n", new[] { s.Diagnoses, s.DiagnosisCodes.Count > 0 ? $"ICD-10: {string.Join(", ", s.DiagnosisCodes)}" : null }
            .Where(part => !string.IsNullOrEmpty(part)));
        var stay = $"{DocumentFormat.Date(s.AdmittedAt)} → {DocumentFormat.Date(s.DischargedAt)}";
        return Html.Of($"""
            {ClinicalDocumentParts.Letterhead(print.Letterhead, logo, ClinicalDocumentParts.Prescriber(print.Consultant, text), template, text)}
            <h2 class="summary-title">{text["doc.discharge.title"]}</h2>
            {status}
            <div class="patient">
              {ClinicalDocumentParts.Field(labels["doc.patient"], p.FullName)}
              {ClinicalDocumentParts.Field("UHID", p.Uhid)}
              {ClinicalDocumentParts.Field(labels["doc.age_sex"], $"{p.Age ?? DocumentFormat.Missing} / {ClinicalDocumentParts.GenderShort(p.Gender)}")}
              {ClinicalDocumentParts.Field(labels["doc.discharge.stay"], stay)}
            </div>
            {ClinicalDocumentParts.TextSection(labels["doc.clinical.diagnosis"], diagnosis)}
            {ClinicalDocumentParts.TextSection(labels["doc.discharge.presenting_complaints"], s.PresentingComplaints)}
            {ClinicalDocumentParts.TextSection(labels["doc.discharge.course"], s.CourseInHospital)}
            {ClinicalDocumentParts.TextSection(labels["doc.discharge.procedures"], s.Procedures)}
            {ClinicalDocumentParts.TextSection(labels["doc.discharge.investigations"], s.InvestigationsSummary)}
            {ClinicalDocumentParts.TextSection(labels["doc.discharge.condition"], s.ConditionAtDischarge)}
            {ClinicalDocumentParts.TextSection(labels["doc.discharge.medicines"], s.Medications)}
            {ClinicalDocumentParts.TextSection(labels["doc.clinical.advice"], s.Advice)}
            {ClinicalDocumentParts.FollowUp(s.FollowUpDate, s.FollowUpInstructions, labels)}
            <div class="footer">
              <div class="small">{text["doc.discharge.emergency"]}</div>
              {ClinicalDocumentParts.Signature(Html.Empty, signature, print.Consultant?.DisplayName ?? s.PractitionerName ?? text["doc.discharge.consultant"], labels)}
            </div>
            """);
    }

    private static bool IsDraft(DischargeSummaryPrint print) => print.Summary.Status == DischargeSummaryStatus.Draft;
}
