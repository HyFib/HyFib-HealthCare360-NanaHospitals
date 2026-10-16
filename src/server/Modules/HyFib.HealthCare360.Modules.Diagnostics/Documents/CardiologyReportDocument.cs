using System.Globalization;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>
/// The A4 cardiology report (CAR-03/04): the measurements of the study type (ECG, echo, TMT, Holter or ABPM; empty ones
/// are left out), the conclusion and recommendations, in the shared report frame with QR verification. Device output
/// (tracings, echo images) is stored with the report and not printed; the notes say how many recordings there are.
/// </summary>
internal static class CardiologyReportDocument
{
    // Internal so the health check-up report, which includes cardiology reports, prints them the same way. The rule is
    // limited to the measurement table, so other tables in the same document keep the frame's style.
    internal const string Styles = """
        .impression { font-weight: 600; }
        .report .body table.measures th { border-bottom: 1px solid #eee; font-weight: 600; color: #333; }

        """;

    public static Task<PrintableDocument> BuildAsync(CardiologyReportView view, ReportDocumentFrame frame, DocumentText text, CancellationToken cancellationToken)
    {
        var name = text.Format("doc.cardiology.document_title", ("number", view.AccessionNumber));
        return frame.BuildAsync(
            new ReportFrame(
                Title(view.StudyType, text),
                name,
                $"cardiology-report-{view.AccessionNumber}",
                ReportDocumentFrame.Footer(view.Patient, name),
                view.Letterhead,
                PatientRows(view, text),
                Body(view, text),
                view.Signatories,
                view.VerificationCode,
                view.VerificationUrl,
                StatusBanner(view.Status, text),
                Notes(view, text),
                Styles,
                text),
            cancellationToken);
    }

    /// <summary>"ECG report", "Echo report"…; a study of another type is a "Cardiology report".</summary>
    public static string Title(CardiologyStudyType type, DocumentText? text = null) => (text ?? DocumentText.English)[type switch
    {
        CardiologyStudyType.Ecg => "doc.cardiology.title.ecg",
        CardiologyStudyType.Echo => "doc.cardiology.title.echo",
        CardiologyStudyType.Tmt => "doc.cardiology.title.tmt",
        CardiologyStudyType.Holter => "doc.cardiology.title.holter",
        CardiologyStudyType.Abpm => "doc.cardiology.title.abpm",
        _ => "doc.cardiology.title.other",
    }];

    /// <summary>Identity, referral and the key times.</summary>
    public static IReadOnlyList<(string Label, string? Value)> PatientRows(CardiologyReportView view, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        return
        [
            .. ReportDocumentFrame.IdentityRows(view.Patient, text),
            (text["doc.accession_number"], view.AccessionNumber),
            (text["doc.referred_by"], view.ReferredBy ?? text["doc.self"]),
            (text["doc.ordered"], DocumentFormat.DateTime(view.OrderedAt)),
            (text["doc.cardiology.recorded"], DocumentFormat.DateTime(view.AcquiredAt)),
            (text["doc.reported"], RadiologyReportDocument.Reported(view.ReleasedAt, view.VerifiedAt, text)),
        ];
    }

    /// <summary>
    /// The study, the critical finding note, the measurements, the conclusion and the recommendations. The measurement
    /// names stay in English, as clinicians write them.
    /// </summary>
    public static Html Body(CardiologyReportView view, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        var rows = MeasurementRows(view);
        var table = rows.Count == 0
            ? Html.Empty
            : Html.Of($"""<table class="measures"><tbody>{rows.Select(r => Html.Of($"<tr><th style=\"width:35%\">{r.Label}</th><td>{r.Value}</td></tr>"))}</tbody></table>""");
        var conclusion = Html.Paragraphs(view.Conclusion);
        var recommendations = Html.Paragraphs(view.Recommendations);
        return Html.Of($"""
            <h2>{view.StudyName}</h2>
            <div class="small">{LongLabel(view.StudyType)}</div>
            {RadiologyReportDocument.CriticalNote(view.IsCriticalFinding, text)}
            {table}
            {(conclusion.IsEmpty ? Html.Empty : Html.Of($"<h3>{text["doc.cardiology.conclusion"]}</h3><div class=\"impression\">{conclusion}</div>"))}
            {(recommendations.IsEmpty ? Html.Empty : Html.Of($"<h3>{text["doc.cardiology.recommendations"]}</h3>{recommendations}"))}
            """);
    }

    /// <summary>
    /// The measurements that belong to the study type, as label and value with units. Values that were not recorded are
    /// left out rather than printed as blanks. A study of another type has none.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> MeasurementRows(CardiologyReportView view) => view.StudyType switch
    {
        CardiologyStudyType.Ecg when view.Ecg is { } e => Compact(
        [
            ("Heart rate", Unit(e.HeartRate, "/min")),
            ("Rhythm", e.Rhythm),
            ("PR interval", Unit(e.PrInterval, "ms")),
            ("QRS duration", Unit(e.QrsDuration, "ms")),
            ("QT interval", Unit(e.QtInterval, "ms")),
            ("QTc", Unit(e.QtcInterval, "ms")),
            ("Axis", e.Axis),
            ("ST-T changes", e.StTChanges),
            ("Interpretation", e.Interpretation),
        ]),
        CardiologyStudyType.Echo when view.Echo is { } e => Compact(
        [
            ("LV ejection fraction", Unit(e.Lvef, "%")),
            ("Left ventricle", e.LeftVentricle),
            ("Right ventricle", e.RightVentricle),
            ("Left atrium", e.LeftAtrium),
            ("Right atrium", e.RightAtrium),
            ("Valves", e.Valves),
            ("Regional wall motion", e.Rwma),
            ("Diastolic function", e.DiastolicFunction),
            ("Pericardium", e.Pericardium),
            ("Echo conclusion", e.Conclusion),
        ]),
        CardiologyStudyType.Tmt when view.Tmt is { } t => Compact(
        [
            ("Protocol", t.Protocol),
            ("Exercise duration", t.DurationSeconds is { } seconds ? $"{Duration(seconds)} min" : null),
            ("Workload", Unit(t.Mets, "METs")),
            ("Maximum heart rate", Unit(t.MaxHeartRate, "/min")),
            ("Target heart rate reached", Unit(t.TargetHeartRatePercent, "%")),
            ("BP response", t.BpResponse),
            ("Symptoms", t.Symptoms),
            ("ST changes", t.StChanges),
            ("Reason for stopping", t.ReasonForTermination),
            ("Result", t.Result is { } result ? TmtResultLabel(result) : null),
        ]),
        CardiologyStudyType.Holter when view.Holter is { } h => Compact(
        [
            ("Recording", Unit(h.DurationHours, "hours")),
            ("Heart rate (min / mean / max)", h.MinHeartRate is null && h.MeanHeartRate is null && h.MaxHeartRate is null
                ? null
                : $"{Plain(h.MinHeartRate)} / {Plain(h.MeanHeartRate)} / {Plain(h.MaxHeartRate)} /min"),
            ("Ventricular ectopics", Unit(h.VentricularEctopics, "")),
            ("Supraventricular ectopics", Unit(h.SupraventricularEctopics, "")),
            ("Pauses", Unit(h.Pauses, "")),
            ("Longest pause", h.LongestPause),
            ("Arrhythmias", h.Arrhythmias),
            ("ST changes", h.StChanges),
        ]),
        CardiologyStudyType.Abpm when view.Abpm is { } a => Compact(
        [
            ("Daytime mean BP", Pair(a.DaytimeMeanSystolic, a.DaytimeMeanDiastolic, "mmHg")),
            ("Night-time mean BP", Pair(a.NighttimeMeanSystolic, a.NighttimeMeanDiastolic, "mmHg")),
            ("Dipping pattern", a.DippingPattern),
        ]),
        _ => [],
    };

    /// <summary>A banner for a copy that is not the final, released report.</summary>
    public static string? StatusBanner(DiagnosticReportStatus status, DocumentText? text = null) => status switch
    {
        DiagnosticReportStatus.Released => null,
        DiagnosticReportStatus.Verified => (text ?? DocumentText.English)["doc.report.signed_not_released"],
        _ => (text ?? DocumentText.English)["doc.cardiology.draft"],
    };

    /// <summary>How many device recordings are stored with the report (they are not printed).</summary>
    public static IReadOnlyList<string> Notes(CardiologyReportView view, DocumentText? text = null) => view.Files.Count switch
    {
        0 => [],
        1 => [(text ?? DocumentText.English)["doc.cardiology.one_recording"]],
        var count => [(text ?? DocumentText.English).Format("doc.cardiology.recordings", ("count", count.ToString(Html.Culture)))],
    };

    public static string LongLabel(CardiologyStudyType type) => type switch
    {
        CardiologyStudyType.Ecg => "12-lead ECG",
        CardiologyStudyType.Echo => "2D echocardiography",
        CardiologyStudyType.Tmt => "Treadmill test (TMT)",
        CardiologyStudyType.Holter => "Holter monitoring",
        CardiologyStudyType.Abpm => "Ambulatory BP monitoring",
        _ => "Other cardiac test",
    };

    public static string TmtResultLabel(TmtResult result) => result switch
    {
        TmtResult.Negative => "Negative for inducible ischaemia",
        TmtResult.Positive => "Positive for inducible ischaemia",
        _ => "Inconclusive",
    };

    private static List<(string Label, string Value)> Compact(IEnumerable<(string Label, string? Value)> rows) =>
        [.. rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).Select(r => (r.Label, r.Value!))];

    // "1,23,456 /min": Indian digit grouping and up to three decimals, as the staff app shows them.
    private static string? Unit(decimal? value, string suffix) =>
        value is { } number ? $"{number.ToString("#,##0.###", Html.Culture)} {suffix}".Trim() : null;

    private static string? Pair(int? first, int? second, string suffix) =>
        first is { } a && second is { } b ? $"{Plain(a)}/{Plain(b)} {suffix}" : null;

    private static string Plain(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? DocumentFormat.Missing;

    // Seconds as "9:05".
    private static string Duration(int seconds) =>
        $"{(seconds / 60).ToString(CultureInfo.InvariantCulture)}:{(seconds % 60).ToString("00", CultureInfo.InvariantCulture)}";
}
