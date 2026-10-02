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

    private const string Note = "This report is a professional opinion and should be correlated clinically.";

    public static Task<PrintableDocument> BuildAsync(RadiologyReportView view, ReportDocumentFrame frame, CancellationToken cancellationToken)
    {
        var name = $"Radiology report {view.AccessionNumber}";
        return frame.BuildAsync(
            new ReportFrame(
                "Radiology report",
                name,
                $"radiology-report-{view.AccessionNumber}",
                ReportDocumentFrame.Footer(view.Patient, name),
                view.Letterhead,
                PatientRows(view),
                Body(view),
                view.Signatories,
                view.VerificationCode,
                view.VerificationUrl,
                StatusBanner(view.Status),
                [Note],
                Styles),
            cancellationToken);
    }

    /// <summary>Identity, referral and the key times.</summary>
    public static IReadOnlyList<(string Label, string? Value)> PatientRows(RadiologyReportView view) =>
    [
        ("Patient", view.Patient.Name),
        ("Age / sex", $"{view.Patient.Age ?? DocumentFormat.Missing} / {LabReportDocument.GenderShort(view.Patient.Gender)}"),
        ("UHID", view.Patient.Uhid),
        ("Accession no.", view.AccessionNumber),
        ("Referred by", view.ReferredBy ?? "Self"),
        ("Ordered", DocumentFormat.DateTime(view.OrderedAt)),
        ("Scanned", DocumentFormat.DateTime(view.AcquiredAt)),
        ("Reported", Reported(view.ReleasedAt, view.VerifiedAt)),
    ];

    /// <summary>The study, the critical finding note, the report's sections in reading order and the addenda.</summary>
    public static Html Body(RadiologyReportView view)
    {
        var detail = view.BodyPart is { Length: > 0 } part ? $"{ModalityLabel(view.Modality)} · {part}" : ModalityLabel(view.Modality);
        var addenda = view.Addenda.Count == 0
            ? Html.Empty
            : Html.Of($"<h2>Addenda</h2>{view.Addenda.Select(Addendum)}");
        return Html.Of($"""
            <h2>{view.StudyName}</h2>
            <div class="small">{detail}</div>
            {CriticalNote(view.IsCriticalFinding)}
            {Block("Clinical history", view.ClinicalHistory)}
            {Block("Technique", view.Technique)}
            {Block("Comparison", view.Comparison)}
            {Block("Findings", view.Findings)}
            {Block("Impression", view.Impression, strong: true)}
            {addenda}
            """);
    }

    /// <summary>A banner for a copy that is not the final, released report.</summary>
    public static string? StatusBanner(DiagnosticReportStatus status) => status switch
    {
        DiagnosticReportStatus.Released => null,
        DiagnosticReportStatus.Verified => "Signed, not yet released",
        _ => "Draft: not signed by the radiologist",
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
    internal static string Reported(DateTimeOffset? releasedAt, DateTimeOffset? verifiedAt) =>
        releasedAt is { } released ? DocumentFormat.DateTime(released)
        : verifiedAt is { } verified ? DocumentFormat.DateTime(verified)
        : "Not signed";

    internal static Html CriticalNote(bool isCriticalFinding) =>
        isCriticalFinding ? Html.Of($"<p class=\"crit\">Critical finding: communicated to the treating doctor.</p>") : Html.Empty;

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

    private static Html Addendum(RadiologyAddendumDto addendum)
    {
        var meta = string.Join(" · ", new[] { DocumentFormat.DateTime(addendum.AddedAt), addendum.AddedBy, addendum.Reason }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var critical = addendum.IsCriticalFinding ? Html.Of($" · <span class=\"crit\">critical finding</span>") : Html.Empty;
        return Html.Of($"<div class=\"addendum keep\"><div class=\"small\">{meta}{critical}</div>{Html.Paragraphs(addendum.Text)}</div>");
    }
}
