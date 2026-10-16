using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>
/// The A4 radiology report (IMG-03): clinical history, technique, comparison, findings and impression, the critical
/// finding note and signed addenda, in the shared report frame with QR verification. A copy that is not signed off, or
/// signed but not released, says so across the page.
/// </summary>
internal static class RadiologyReportDocument
{
    // Internal so the health check-up report, which includes radiology reports, prints them the same way.
    internal const string Styles = """
        .impression { font-weight: 600; }
        .addendum { border-left: 3px solid #999; padding-left: 8px; margin-bottom: 6px; }

        """;

    public static Task<PrintableDocument> BuildAsync(RadiologyReportView view, ReportDocumentFrame frame, DocumentText text, CancellationToken cancellationToken)
    {
        var name = text.Format("doc.radiology.document_title", ("number", view.AccessionNumber));
        return frame.BuildAsync(
            new ReportFrame(
                text["doc.radiology.title"],
                name,
                $"radiology-report-{view.AccessionNumber}",
                ReportDocumentFrame.Footer(view.Patient, name),
                view.Letterhead,
                PatientRows(view, text),
                Body(view, text),
                view.Signatories,
                view.VerificationCode,
                view.VerificationUrl,
                StatusBanner(view.Status, text),
                [text["doc.radiology.note"]],
                Styles,
                text),
            cancellationToken);
    }

    /// <summary>Identity, referral and the key times.</summary>
    public static IReadOnlyList<(string Label, string? Value)> PatientRows(RadiologyReportView view, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        return
        [
            .. ReportDocumentFrame.IdentityRows(view.Patient, text),
            (text["doc.accession_number"], view.AccessionNumber),
            (text["doc.referred_by"], view.ReferredBy ?? text["doc.self"]),
            (text["doc.ordered"], DocumentFormat.DateTime(view.OrderedAt)),
            (text["doc.radiology.scanned"], DocumentFormat.DateTime(view.AcquiredAt)),
            (text["doc.reported"], Reported(view.ReleasedAt, view.VerifiedAt, text)),
        ];
    }

    /// <summary>The study, the critical finding note, the report's sections in reading order and the addenda.</summary>
    public static Html Body(RadiologyReportView view, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        var detail = view.BodyPart is { Length: > 0 } part ? $"{ModalityLabel(view.Modality)} · {part}" : ModalityLabel(view.Modality);
        var addenda = view.Addenda.Count == 0
            ? Html.Empty
            : Html.Of($"<h2>{text["doc.radiology.addenda"]}</h2>{view.Addenda.Select(addendum => Addendum(addendum, text))}");
        return Html.Of($"""
            <h2>{view.StudyName}</h2>
            <div class="small">{detail}</div>
            {CriticalNote(view.IsCriticalFinding, text)}
            {Block(text["doc.radiology.clinical_history"], view.ClinicalHistory)}
            {Block(text["doc.radiology.technique"], view.Technique)}
            {Block(text["doc.radiology.comparison"], view.Comparison)}
            {Block(text["doc.radiology.findings"], view.Findings)}
            {Block(text["doc.radiology.impression"], view.Impression, strong: true)}
            {addenda}
            """);
    }

    /// <summary>A banner for a copy that is not the final, released report.</summary>
    public static string? StatusBanner(DiagnosticReportStatus status, DocumentText? text = null) => status switch
    {
        DiagnosticReportStatus.Released => null,
        DiagnosticReportStatus.Verified => (text ?? DocumentText.English)["doc.report.signed_not_released"],
        _ => (text ?? DocumentText.English)["doc.radiology.draft"],
    };

    public static string ModalityLabel(Modality modality) => modality switch
    {
        Modality.XR => "X-ray",
        Modality.CT => "CT scan",
        Modality.MR => "MRI",
        Modality.US => "Ultrasound",
        Modality.MG => "Mammography",
        Modality.RF => "Fluoroscopy",
        Modality.DX => "Digital X-ray",
        _ => modality.ToString(),
    };

    /// <summary>When the report was released, or else signed off ("Not signed" before sign-off).</summary>
    internal static string Reported(DateTimeOffset? releasedAt, DateTimeOffset? verifiedAt, DocumentText text) =>
        releasedAt is { } released ? DocumentFormat.DateTime(released)
        : verifiedAt is { } verified ? DocumentFormat.DateTime(verified)
        : text["doc.not_signed"];

    internal static Html CriticalNote(bool isCriticalFinding, DocumentText text) =>
        isCriticalFinding ? Html.Of($"<p class=\"crit\">{text["doc.report.critical_note"]}</p>") : Html.Empty;

    // A heading and its text; nothing when the section was left empty.
    private static Html Block(string title, string? text, bool strong = false)
    {
        var paragraphs = Html.Paragraphs(text);
        if (paragraphs.IsEmpty)
        {
            return Html.Empty;
        }

        return strong ? Html.Of($"<h3>{title}</h3><div class=\"impression\">{paragraphs}</div>") : Html.Of($"<h3>{title}</h3><div>{paragraphs}</div>");
    }

    private static Html Addendum(RadiologyAddendumDto addendum, DocumentText text)
    {
        var meta = string.Join(" · ", new[] { DocumentFormat.DateTime(addendum.AddedAt), addendum.AddedBy, addendum.Reason }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var critical = addendum.IsCriticalFinding ? Html.Of($" · <span class=\"crit\">{text["doc.radiology.critical_finding"]}</span>") : Html.Empty;
        return Html.Of($"<div class=\"addendum keep\"><div class=\"small\">{meta}{critical}</div>{Html.Paragraphs(addendum.Text)}</div>");
    }
}
