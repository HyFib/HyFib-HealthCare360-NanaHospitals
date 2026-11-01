using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>
/// The A4 lab report (LIS-07): sections, tests and analytes with flag, unit and reference interval; cultures with the
/// organism and antibiogram; interpretations, amendments and footnotes, in the shared report frame with QR
/// verification. A value that is not verified is never printed as a result on a released copy. Headings and labels are
/// in the language it is made in (PP-06); test names, values, units, reference intervals and flag codes print as they are.
/// </summary>
internal static class LabReportDocument
{
    // Internal so the health check-up report, which includes lab results, prints them the same way.
    internal const string Styles = """
        .group { break-after: avoid; }
        .abg { width: auto; min-width: 60%; margin: 4px 0; }
        .abg th, .abg td { padding: 1px 6px; font-size: 9pt; }

        """;

    /// <param name="releasedOnly">A patient's or clinician's copy: only verified values are printed.</param>
    public static Task<PrintableDocument> BuildAsync(
        LabReportView view, bool releasedOnly, ReportDocumentFrame frame, DocumentText text, CancellationToken cancellationToken)
    {
        var released = view.Status is LabReportStatus.Released or LabReportStatus.Amended;
        var name = text.Format("doc.lab.document_title", ("number", view.ReportNumber));
        return frame.BuildAsync(
            new ReportFrame(
                text["doc.lab.title"],
                name,
                $"lab-report-{view.ReportNumber}",
                ReportDocumentFrame.Footer(view.Patient, name),
                view.Letterhead,
                PatientRows(view, text),
                Body(view, releasedOnly, text),
                view.Signatories,
                view.VerificationCode,
                view.VerificationUrl,
                released ? null : text["doc.lab.preliminary"],
                Notes(view, text),
                Styles,
                text),
            cancellationToken);
    }

    /// <summary>Identity, referral and the key times.</summary>
    public static IReadOnlyList<(string Label, string? Value)> PatientRows(LabReportView view, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        var tests = view.Sections.SelectMany(s => s.Tests).ToList();
        var collected = tests.Select(t => t.CollectedAt).Where(t => t is not null).Min();
        var received = tests.Select(t => t.ReceivedAt).Where(t => t is not null).Min();
        return
        [
            .. ReportDocumentFrame.IdentityRows(view.Patient, text),
            (text["doc.report_number"], view.Revision > 1
                ? text.Format("doc.report_revision", ("number", view.ReportNumber), ("revision", view.Revision.ToString(Html.Culture)))
                : view.ReportNumber),
            (text["doc.referred_by"], view.ReferredBy ?? text["doc.self"]),
            (text["doc.ordered"], DocumentFormat.DateTime(view.OrderedAt)),
            (text["doc.lab.collected"], DocumentFormat.DateTime(collected)),
            (text["doc.lab.received"], DocumentFormat.DateTime(received)),
            (text["doc.lab.released"], view.ReleasedAt is { } at ? DocumentFormat.DateTime(at) : text["doc.lab.not_released"]),
        ];
    }

    public static Html Body(LabReportView view, bool releasedOnly, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        var sections = view.Sections.Select(section =>
            Html.Of($"<h2>{SectionLabel(section.Section)}</h2>{section.Tests.Select(test => Test(test, releasedOnly, text))}"));
        var amendments = view.Amendments.Count == 0
            ? Html.Empty
            : Html.Of($"""
                <h2>{text["doc.lab.amendments"]}</h2>
                <table><thead><tr><th>{text["doc.lab.revision"]}</th><th>{text["doc.lab.when"]}</th><th>{text["doc.lab.by"]}</th><th>{text["doc.lab.reason_changes"]}</th></tr></thead><tbody>
                {view.Amendments.Select(a => Html.Of($"<tr><td>{a.Revision}</td><td>{DocumentFormat.DateTime(a.AmendedAt)}</td><td>{a.AmendedBy}{AskedBy(a, text)}</td><td>{a.Reason}<div class=\"small\">{a.Changes}</div></td></tr>"))}
                </tbody></table>
                """);
        return Html.Join(sections) + amendments;
    }

    /// <summary>A correction waiting for approval, partial report, auto-validation and amendment notices.</summary>
    public static IReadOnlyList<string> Notes(LabReportView view, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        var notes = new List<string>();
        if (view.AmendmentWaitingSince is not null)
        {
            notes.Add(text["doc.lab.note.amendment_waiting"]);
        }

        if (view.IsPartial)
        {
            notes.Add(text["doc.lab.note.partial"]);
        }

        if (view.HasAutoValidatedResults)
        {
            notes.Add(text["doc.lab.note.auto_validated"]);
        }

        if (view.Status == LabReportStatus.Amended)
        {
            notes.Add(text["doc.lab.note.amended"]);
        }

        return notes;
    }

    /// <summary>Who asked for a correction a second pathologist released, under the name of the one who released it.</summary>
    private static Html AskedBy(LabAmendmentView amendment, DocumentText text) =>
        amendment.RequestedBy is { } name ? Html.Of($"<div class=\"small\">{text.Format("doc.lab.asked_by", ("name", name))}</div>") : Html.Empty;

    public static string SectionLabel(LabSection section) => section switch
    {
        LabSection.Hematology => "Haematology",
        LabSection.ClinicalPathology => "Clinical pathology",
        _ => section.ToString(),
    };

    private static Html Test(LabReportTest test, bool releasedOnly, DocumentText text)
    {
        var meta = string.Join(" · ", new[]
        {
            SpecimenLabel(test.SpecimenType),
            test.Method is { } method ? text.Format("doc.lab.method", ("method", method)) : null,
            test.ReferralLabName is { } lab ? text.Format("doc.lab.tested_at", ("lab", lab)) : null,
        }.Where(part => part is not null));
        var status = test.Status switch
        {
            LabOrderItemStatus.Released => Html.Empty,
            LabOrderItemStatus.Verified => Html.Of($" <span class=\"small\">{text["doc.lab.verified_not_released"]}</span>"),
            _ => Html.Of($" <span class=\"small\">{text["doc.lab.in_progress"]}</span>"),
        };

        var rows = new List<Html>();
        string? lastGroup = null;
        foreach (var line in test.Results)
        {
            if (line.GroupName is { } group && group != lastGroup)
            {
                rows.Add(Html.Of($"<tbody class=\"group\"><tr><td colspan=\"5\"><strong>{group}</strong></td></tr></tbody>"));
            }

            lastGroup = line.GroupName;

            // Organism, antibiogram and remarks are part of the result: never shown before verification on a released copy.
            var printable = line.Status == LabResultStatus.Verified || (!releasedOnly && line.Status != LabResultStatus.Pending);
            var culture = printable ? Culture(line, text) : Html.Empty;
            var remarks = printable && line.Remarks is { Length: > 0 } remark ? Html.Of($"<div class=\"small\">{remark}</div>") : Html.Empty;
            var flag = line.Status == LabResultStatus.Verified || !releasedOnly ? Flag(line.Flag, text) : Html.Empty;
            // A result and its culture details are one group, so a page never separates them.
            var details = culture.IsEmpty ? Html.Empty : Html.Of($"<tr><td></td><td colspan=\"4\">{culture}</td></tr>");
            rows.Add(Html.Of($"<tbody class=\"keep\"><tr><td>{line.Name}</td><td>{Value(line, releasedOnly, text)}{remarks}</td><td>{flag}</td><td>{line.Unit}</td><td>{line.ReferenceText}</td></tr>{details}</tbody>"));
        }

        return Html.Of($"""
            <h3>{test.Name}{status}</h3>
            {(meta.Length == 0 ? Html.Empty : Html.Of($"<div class=\"small keep-next\">{meta}</div>"))}
            <table>
              <thead><tr><th style="width:34%">{text["doc.lab.investigation"]}</th><th style="width:22%">{text["doc.lab.result"]}</th><th style="width:7%">{text["doc.lab.flag"]}</th><th style="width:12%">{text["doc.lab.unit"]}</th><th>{text["doc.lab.reference_interval"]}</th></tr></thead>
              {rows}
            </table>
            {(test.Interpretation is { Length: > 0 } interpretation ? Html.Of($"<div class=\"small\"><strong>{text["doc.lab.interpretation"]}:</strong> {interpretation}</div>") : Html.Empty)}
            """);
    }

    private static Html Value(LabReportResultLine line, bool releasedOnly, DocumentText text)
    {
        if (line.Status != LabResultStatus.Verified && (releasedOnly || line.Status == LabResultStatus.Pending))
        {
            return Html.Of($"<span class=\"small\">{text["doc.lab.awaited"]}</span>");
        }

        var (code, _, critical) = FlagDisplay(line.Flag);
        var css = critical ? "crit" : code.Length > 0 ? "abn" : "";
        var star = line.AutoValidated ? "*" : "";
        var provisional = line.Status != LabResultStatus.Verified ? Html.Of($" <span class=\"small\">{text["doc.lab.not_verified"]}</span>") : Html.Empty;
        return Html.Of($"<span class=\"{css}\">{line.Value}{star}</span>{provisional}");
    }

    private static Html Flag(ResultFlag? flag, DocumentText text)
    {
        var (code, label, critical) = FlagDisplay(flag);
        return code.Length == 0 ? Html.Empty : Html.Of($"<span class=\"{(critical ? "crit" : "flag")}\" title=\"{text[label]}\">{code}</span>");
    }

    private static Html Culture(LabReportResultLine line, DocumentText text)
    {
        var organism = line.Organism is { Length: > 0 } name
            ? Html.Of($"<div>{text["doc.lab.organism"]}: <strong>{name}</strong>{(line.ColonyCount is { Length: > 0 } count ? $" · {text.Format("doc.lab.colony_count", ("count", count))}" : "")}</div>")
            : Html.Empty;
        var antibiogram = line.Susceptibilities.Count == 0
            ? Html.Empty
            : Html.Of($"""
                <table class="abg"><thead><tr><th>{text["doc.lab.antibiotic"]}</th><th>{text["doc.lab.interpretation"]}</th><th>MIC</th></tr></thead><tbody>
                {line.Susceptibilities.Select(s => Html.Of($"<tr><td>{s.Antibiotic}</td><td class=\"{(s.Interpretation == SusceptibilityInterpretation.Resistant ? "abn" : "")}\">{Interpretation(s.Interpretation, text)}</td><td>{s.Mic}</td></tr>"))}
                </tbody></table>
                """);
        return organism + antibiogram;
    }

    // The printed flag code, the key of the words behind it and whether it is critical; colour is never the only signal.
    private static (string Code, string Label, bool Critical) FlagDisplay(ResultFlag? flag) => flag switch
    {
        ResultFlag.Low => ("L", "doc.lab.flag.low", false),
        ResultFlag.High => ("H", "doc.lab.flag.high", false),
        ResultFlag.CriticalLow => ("LL", "doc.lab.flag.critically_low", true),
        ResultFlag.CriticalHigh => ("HH", "doc.lab.flag.critically_high", true),
        ResultFlag.Abnormal => ("A", "doc.lab.flag.abnormal", false),
        ResultFlag.Critical => ("C", "doc.lab.flag.critical", true),
        _ => ("", "", false),
    };

    private static string Interpretation(SusceptibilityInterpretation interpretation, DocumentText text) => text[interpretation switch
    {
        SusceptibilityInterpretation.Sensitive => "doc.lab.sensitive",
        SusceptibilityInterpretation.Intermediate => "doc.lab.intermediate",
        _ => "doc.lab.resistant",
    }];

    private static string SpecimenLabel(SpecimenType specimen) => specimen switch
    {
        SpecimenType.WholeBlood => "Whole blood",
        SpecimenType.Csf => "CSF",
        SpecimenType.BodyFluid => "Body fluid",
        _ => specimen.ToString(),
    };

    internal static string GenderShort(Gender gender) => gender switch
    {
        Gender.Male => "M",
        Gender.Female => "F",
        Gender.Other => "O",
        _ => "U",
    };
}
