using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>
/// The A4 lab report (LIS-07): sections, tests and analytes with flag, unit and reference interval; cultures with the
/// organism and antibiogram; interpretations, amendments and footnotes, in the shared report frame with QR
/// verification. A value that is not verified is never printed as a result on a released copy.
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
    public static Task<PrintableDocument> BuildAsync(LabReportView view, bool releasedOnly, ReportDocumentFrame frame, CancellationToken cancellationToken)
    {
        var released = view.Status is LabReportStatus.Released or LabReportStatus.Amended;
        var name = $"Lab report {view.ReportNumber}";
        return frame.BuildAsync(
            new ReportFrame(
                "Laboratory report",
                name,
                $"lab-report-{view.ReportNumber}",
                ReportDocumentFrame.Footer(view.Patient, name),
                view.Letterhead,
                PatientRows(view),
                Body(view, releasedOnly),
                view.Signatories,
                view.VerificationCode,
                view.VerificationUrl,
                released ? null : "Preliminary copy for the laboratory: not released",
                Notes(view),
                Styles),
            cancellationToken);
    }

    /// <summary>Identity, referral and the key times.</summary>
    public static IReadOnlyList<(string Label, string? Value)> PatientRows(LabReportView view)
    {
        var tests = view.Sections.SelectMany(s => s.Tests).ToList();
        var collected = tests.Select(t => t.CollectedAt).Where(t => t is not null).Min();
        var received = tests.Select(t => t.ReceivedAt).Where(t => t is not null).Min();
        return
        [
            ("Patient", view.Patient.Name),
            ("Age / sex", $"{view.Patient.Age ?? DocumentFormat.Missing} / {GenderShort(view.Patient.Gender)}"),
            ("UHID", view.Patient.Uhid),
            ("Report no.", view.Revision > 1 ? $"{view.ReportNumber} (revision {view.Revision})" : view.ReportNumber),
            ("Referred by", view.ReferredBy ?? "Self"),
            ("Ordered", DocumentFormat.DateTime(view.OrderedAt)),
            ("Collected", DocumentFormat.DateTime(collected)),
            ("Received", DocumentFormat.DateTime(received)),
            ("Released", view.ReleasedAt is { } at ? DocumentFormat.DateTime(at) : "Not released"),
        ];
    }

    public static Html Body(LabReportView view, bool releasedOnly)
    {
        var sections = view.Sections.Select(section =>
            Html.Of($"<h2>{SectionLabel(section.Section)}</h2>{section.Tests.Select(test => Test(test, releasedOnly))}"));
        var amendments = view.Amendments.Count == 0
            ? Html.Empty
            : Html.Of($"""
                <h2>Amendments</h2>
                <table><thead><tr><th>Revision</th><th>When</th><th>By</th><th>Reason and changes</th></tr></thead><tbody>
                {view.Amendments.Select(a => Html.Of($"<tr><td>{a.Revision}</td><td>{DocumentFormat.DateTime(a.AmendedAt)}</td><td>{a.AmendedBy}</td><td>{a.Reason}<div class=\"small\">{a.Changes}</div></td></tr>"))}
                </tbody></table>
                """);
        return Html.Join(sections) + amendments;
    }

    /// <summary>Partial report, auto-validation and amendment notices.</summary>
    public static IReadOnlyList<string> Notes(LabReportView view)
    {
        var notes = new List<string>();
        if (view.IsPartial)
        {
            notes.Add("Partial report: some tests are still in progress and will follow in an updated report.");
        }

        if (view.HasAutoValidatedResults)
        {
            notes.Add("* Verified automatically under the laboratory auto-validation rules (in range, QC accepted, delta check passed).");
        }

        if (view.Status == LabReportStatus.Amended)
        {
            notes.Add("This report was amended after release. The amendments and their reasons are listed above.");
        }

        return notes;
    }

    public static string SectionLabel(LabSection section) => section switch
    {
        LabSection.Hematology => "Haematology",
        LabSection.ClinicalPathology => "Clinical pathology",
        _ => section.ToString(),
    };

    private static Html Test(LabReportTest test, bool releasedOnly)
    {
        var meta = string.Join(" · ", new[]
        {
            SpecimenLabel(test.SpecimenType),
            test.Method is { } method ? $"Method: {method}" : null,
            test.ReferralLabName is { } lab ? $"Tested at {lab}" : null,
        }.Where(part => part is not null));
        var status = test.Status switch
        {
            LabOrderItemStatus.Released => Html.Empty,
            LabOrderItemStatus.Verified => Html.Of($" <span class=\"small\">(verified, not yet released)</span>"),
            _ => Html.Of($" <span class=\"small\">(in progress)</span>"),
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
            var culture = printable ? Culture(line) : Html.Empty;
            var remarks = printable && line.Remarks is { Length: > 0 } text ? Html.Of($"<div class=\"small\">{text}</div>") : Html.Empty;
            var flag = line.Status == LabResultStatus.Verified || !releasedOnly ? Flag(line.Flag) : Html.Empty;
            // A result and its culture details are one group, so a page never separates them.
            var details = culture.IsEmpty ? Html.Empty : Html.Of($"<tr><td></td><td colspan=\"4\">{culture}</td></tr>");
            rows.Add(Html.Of($"<tbody class=\"keep\"><tr><td>{line.Name}</td><td>{Value(line, releasedOnly)}{remarks}</td><td>{flag}</td><td>{line.Unit}</td><td>{line.ReferenceText}</td></tr>{details}</tbody>"));
        }

        return Html.Of($"""
            <h3>{test.Name}{status}</h3>
            {(meta.Length == 0 ? Html.Empty : Html.Of($"<div class=\"small keep-next\">{meta}</div>"))}
            <table>
              <thead><tr><th style="width:34%">Investigation</th><th style="width:22%">Result</th><th style="width:7%">Flag</th><th style="width:12%">Unit</th><th>Reference interval</th></tr></thead>
              {rows}
            </table>
            {(test.Interpretation is { Length: > 0 } interpretation ? Html.Of($"<div class=\"small\"><strong>Interpretation:</strong> {interpretation}</div>") : Html.Empty)}
            """);
    }

    private static Html Value(LabReportResultLine line, bool releasedOnly)
    {
        if (line.Status != LabResultStatus.Verified && (releasedOnly || line.Status == LabResultStatus.Pending))
        {
            return Html.Of($"<span class=\"small\">Awaited</span>");
        }

        var (code, _, critical) = FlagDisplay(line.Flag);
        var css = critical ? "crit" : code.Length > 0 ? "abn" : "";
        var star = line.AutoValidated ? "*" : "";
        var provisional = line.Status != LabResultStatus.Verified ? Html.Of($" <span class=\"small\">(not verified)</span>") : Html.Empty;
        return Html.Of($"<span class=\"{css}\">{line.Value}{star}</span>{provisional}");
    }

    private static Html Flag(ResultFlag? flag)
    {
        var (code, label, critical) = FlagDisplay(flag);
        return code.Length == 0 ? Html.Empty : Html.Of($"<span class=\"{(critical ? "crit" : "flag")}\" title=\"{label}\">{code}</span>");
    }

    private static Html Culture(LabReportResultLine line)
    {
        var organism = line.Organism is { Length: > 0 } name
            ? Html.Of($"<div>Organism isolated: <strong>{name}</strong>{(line.ColonyCount is { Length: > 0 } count ? $" · Colony count {count}" : "")}</div>")
            : Html.Empty;
        var antibiogram = line.Susceptibilities.Count == 0
            ? Html.Empty
            : Html.Of($"""
                <table class="abg"><thead><tr><th>Antibiotic</th><th>Interpretation</th><th>MIC</th></tr></thead><tbody>
                {line.Susceptibilities.Select(s => Html.Of($"<tr><td>{s.Antibiotic}</td><td class=\"{(s.Interpretation == SusceptibilityInterpretation.Resistant ? "abn" : "")}\">{Interpretation(s.Interpretation)}</td><td>{s.Mic}</td></tr>"))}
                </tbody></table>
                """);
        return organism + antibiogram;
    }

    // The printed flag code, the words behind it and whether it is critical; colour is never the only signal.
    private static (string Code, string Label, bool Critical) FlagDisplay(ResultFlag? flag) => flag switch
    {
        ResultFlag.Low => ("L", "Low", false),
        ResultFlag.High => ("H", "High", false),
        ResultFlag.CriticalLow => ("LL", "Critically low", true),
        ResultFlag.CriticalHigh => ("HH", "Critically high", true),
        ResultFlag.Abnormal => ("A", "Abnormal", false),
        ResultFlag.Critical => ("C", "Critical", true),
        _ => ("", "", false),
    };

    private static string Interpretation(SusceptibilityInterpretation interpretation) => interpretation switch
    {
        SusceptibilityInterpretation.Sensitive => "Sensitive (S)",
        SusceptibilityInterpretation.Intermediate => "Intermediate (I)",
        _ => "Resistant (R)",
    };

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
