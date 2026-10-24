using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Released diagnostic reports → FHIR <c>DiagnosticReport</c>:
/// <list type="bullet">
/// <item>laboratory (<c>lab-</c>): one per accession, LOINC 11502-2, <c>result</c> → the released lab Observations;
/// partial while other tests are pending, amended after a post-release correction;</item>
/// <item>radiology (<c>rad-</c>): LOINC 18748-4 plus the study, the impression (and any addenda) as <c>conclusion</c> and
/// the full text as <c>presentedForm</c>; <c>appended</c> once addenda exist;</item>
/// <item>cardiology (<c>card-</c>): ECG/Holter/TMT (EC) or echo (CUS) with the structured measurements as text.</item>
/// </list>
/// Drafts, unverified and unreleased reports are never shared.
/// </summary>
internal static class DiagnosticReportMapper
{
    public const string LaboratoryReportLoinc = "11502-2";
    public const string ImagingStudyLoinc = "18748-4";
    public const string EcgLoinc = "11524-6";
    public const string EchoLoinc = "59281-6";
    public const string ExerciseStressTestLoinc = "18752-6";
    public const string HolterLoinc = "18754-2";

    public static R4.DiagnosticReport FromLab(LabReportRow r)
    {
        var testNames = r.TestNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        var report = new R4.DiagnosticReport
        {
            Id = FhirIds.Of(FhirIds.Lab, r.Id),
            Meta = FhirElements.Meta(r.CreatedAt, r.UpdatedAt),
            Status = r.Status == LabReportStatus.Amended ? R4.DiagnosticReport.DiagnosticReportStatus.Amended
                : r.IsPartial ? R4.DiagnosticReport.DiagnosticReportStatus.Partial
                : R4.DiagnosticReport.DiagnosticReportStatus.Final,
            Category = [Section("LAB", "Laboratory")],
            Code = new R4.CodeableConcept
            {
                Coding = [FhirElements.Coding(FhirSystems.Loinc, LaboratoryReportLoinc, "Laboratory report")],
                Text = testNames.Count > 0 ? string.Join(", ", testNames) : "Laboratory report",
            },
            Subject = FhirReferences.Patient(r.PatientId),
            Encounter = r.VisitId is { } visit ? FhirReferences.Encounter(FhirIds.Visit, visit) : null,
            Effective = new R4.FhirDateTime(FhirTime.DateTime(r.CollectedAt ?? r.OrderedAt)),
            Issued = r.ReleasedAt is { } released ? FhirTime.ToMilliseconds(released) : null,
            Result = r.ResultIds.Select(id => FhirReferences.Observation(FhirIds.Of(FhirIds.Lab, id))).ToList(),
        };

        report.Identifier.Add(Accession(r.ReportNumber));
        return report;
    }

    public static R4.DiagnosticReport FromRadiology(RadiologyReportRow r)
    {
        var report = new R4.DiagnosticReport
        {
            Id = FhirIds.Of(FhirIds.Radiology, r.Id),
            Meta = FhirElements.Meta(r.CreatedAt, r.UpdatedAt),
            Status = r.Addenda.Count > 0 ? R4.DiagnosticReport.DiagnosticReportStatus.Appended : R4.DiagnosticReport.DiagnosticReportStatus.Final,
            Category = [Section("RAD", "Radiology")],
            Code = new R4.CodeableConcept
            {
                Coding = [FhirElements.Coding(FhirSystems.Loinc, ImagingStudyLoinc, "Diagnostic imaging study"), ServiceCoding(r.ServiceCode, r.StudyName)],
                Text = r.StudyName,
            },
            Subject = FhirReferences.Patient(r.PatientId),
            Encounter = r.VisitId is { } visit ? FhirReferences.Encounter(FhirIds.Visit, visit) : null,
            Effective = new R4.FhirDateTime(FhirTime.DateTime(r.AcquiredAt ?? r.OrderedAt)),
            Issued = r.ReleasedAt is { } released ? FhirTime.ToMilliseconds(released) : null,
            Conclusion = RadiologyConclusion(r),
            PresentedForm = [TextAttachment(RadiologyText(r), "Radiology report", r.ReleasedAt)],
        };

        report.Identifier.Add(Accession(r.AccessionNumber));
        return report;
    }

    public static R4.DiagnosticReport FromCardiology(CardiologyReportRow r)
    {
        var code = new R4.CodeableConcept { Text = r.StudyName };
        if (CardiologyStudyCoding(r.StudyType, r.StudyName) is { } study)
        {
            code.Coding.Add(study);
        }

        code.Coding.Add(ServiceCoding(r.ServiceCode, r.StudyName));
        var report = new R4.DiagnosticReport
        {
            Id = FhirIds.Of(FhirIds.Cardiology, r.Id),
            Meta = FhirElements.Meta(r.CreatedAt, r.UpdatedAt),
            Status = R4.DiagnosticReport.DiagnosticReportStatus.Final,
            Category = [CardiologySection(r.StudyType)],
            Code = code,
            Subject = FhirReferences.Patient(r.PatientId),
            Encounter = r.VisitId is { } visit ? FhirReferences.Encounter(FhirIds.Visit, visit) : null,
            Effective = new R4.FhirDateTime(FhirTime.DateTime(r.AcquiredAt ?? r.OrderedAt)),
            Issued = r.ReleasedAt is { } released ? FhirTime.ToMilliseconds(released) : null,
            Conclusion = FhirElements.Clean(r.Conclusion),
            PresentedForm = [TextAttachment(CardiologyText(r), "Cardiology report", r.ReleasedAt)],
        };

        report.Identifier.Add(Accession(r.AccessionNumber));
        return report;
    }

    /// <summary>
    /// The LOINC study a cardiology report is of, with LOINC's name for it: an ECG, a transthoracic echo (the hospital's
    /// "2D echo"; a transoesophageal one is another LOINC study, so it gets none here rather than a wrong one), an exercise
    /// stress test (TMT) or a Holter monitor study. None for ambulatory blood pressure monitoring or any other study.
    /// </summary>
    public static R4.Coding? CardiologyStudyCoding(CardiologyStudyType type, string studyName) => type switch
    {
        CardiologyStudyType.Ecg => FhirElements.Coding(FhirSystems.Loinc, EcgLoinc, "EKG study"),
        CardiologyStudyType.Echo when !Transoesophageal(studyName) =>
            FhirElements.Coding(FhirSystems.Loinc, EchoLoinc, "Transthoracic cardiac echo study report US"),
        CardiologyStudyType.Tmt => FhirElements.Coding(FhirSystems.Loinc, ExerciseStressTestLoinc, "Exercise stress test study"),
        CardiologyStudyType.Holter => FhirElements.Coding(FhirSystems.Loinc, HolterLoinc, "Holter monitor study"),
        _ => null,
    };

    private static bool Transoesophageal(string studyName) =>
        studyName.Contains("oesophag", StringComparison.OrdinalIgnoreCase) || studyName.Contains("esophag", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(studyName, @"\bTEE\b", RegexOptions.IgnoreCase);

    /// <summary>Diagnostic service section (v2-0074): EC for ECG, Holter and TMT, CUS for echo, OTH otherwise.</summary>
    public static R4.CodeableConcept CardiologySection(CardiologyStudyType type) => type switch
    {
        CardiologyStudyType.Ecg or CardiologyStudyType.Holter or CardiologyStudyType.Tmt => Section("EC", "Electrocardiac (e.g., EKG, EEC, Holter)"),
        CardiologyStudyType.Echo => Section("CUS", "Cardiac Ultrasound"),
        _ => Section("OTH", "Other"),
    };

    /// <summary>
    /// The impression, then each addendum in the order they were added. A reader may see only the conclusion, and an
    /// addendum may correct the impression (an effusion found on a second read of a film reported as normal).
    /// </summary>
    public static string? RadiologyConclusion(RadiologyReportRow r)
    {
        List<string> parts = [.. new[] { FhirElements.Clean(r.Impression) }.OfType<string>()];
        foreach (var addendum in r.Addenda.OrderBy(a => a.AddedAt))
        {
            if (FhirElements.Clean(addendum.Text) is { } text)
            {
                parts.Add($"Addendum {FhirTime.DateTime(addendum.AddedAt)}: {text}");
            }
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>The study, its modality and body part (as the printed report heads it), the report's sections and addenda.</summary>
    public static string RadiologyText(RadiologyReportRow r)
    {
        var text = new ReportText();
        text.Line(r.StudyName);
        text.Field("Modality", ModalityName(r.Modality));
        text.Field("Body part", r.BodyPart);
        text.Field("Technique", r.Technique);
        text.Field("Clinical history", r.ClinicalHistory);
        text.Field("Comparison", r.Comparison);
        text.Field("Findings", r.Findings);
        text.Field("Impression", r.Impression);
        if (r.IsCriticalFinding)
        {
            text.Line("Critical finding: communicated to the treating clinician.");
        }

        foreach (var addendum in r.Addenda.OrderBy(a => a.AddedAt))
        {
            var reason = FhirElements.Clean(addendum.Reason) is { } value ? $" (reason: {value})" : string.Empty;
            text.Field($"Addendum {FhirTime.DateTime(addendum.AddedAt)}{reason}", addendum.Text);
        }

        return text.ToString();
    }

    /// <summary>The modality as the printed radiology report names it ("X-ray", "CT scan", "MRI").</summary>
    public static string ModalityName(Modality modality) => modality switch
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

    /// <summary>The measurements of the study type, then the conclusion and recommendations.</summary>
    public static string CardiologyText(CardiologyReportRow r)
    {
        var text = new ReportText();
        text.Line(r.StudyName);
        switch (r.StudyType)
        {
            case CardiologyStudyType.Ecg:
                text.Field("Heart rate", Unit(r.Ecg.HeartRate, "bpm"));
                text.Field("Rhythm", r.Ecg.Rhythm);
                text.Field("PR interval", Unit(r.Ecg.PrInterval, "ms"));
                text.Field("QRS duration", Unit(r.Ecg.QrsDuration, "ms"));
                text.Field("QT interval", Unit(r.Ecg.QtInterval, "ms"));
                text.Field("QTc interval", Unit(r.Ecg.QtcInterval, "ms"));
                text.Field("Axis", r.Ecg.Axis);
                text.Field("ST-T changes", r.Ecg.StTChanges);
                text.Field("Interpretation", r.Ecg.Interpretation);
                break;
            case CardiologyStudyType.Echo:
                text.Field("LVEF", r.Echo.Lvef is { } lvef ? $"{FhirElements.Normalize(lvef).ToString(CultureInfo.InvariantCulture)} %" : null);
                text.Field("Left ventricle", r.Echo.LeftVentricle);
                text.Field("Right ventricle", r.Echo.RightVentricle);
                text.Field("Left atrium", r.Echo.LeftAtrium);
                text.Field("Right atrium", r.Echo.RightAtrium);
                text.Field("Valves", r.Echo.Valves);
                text.Field("Regional wall motion", r.Echo.Rwma);
                text.Field("Diastolic function", r.Echo.DiastolicFunction);
                text.Field("Pericardium", r.Echo.Pericardium);
                text.Field("Echo conclusion", r.Echo.Conclusion);
                break;
            case CardiologyStudyType.Tmt:
                text.Field("Protocol", r.Tmt.Protocol);
                text.Field("Exercise duration", r.Tmt.DurationSeconds is { } seconds ? $"{seconds / 60} min {seconds % 60} s" : null);
                text.Field("METs", r.Tmt.Mets is { } mets ? FhirElements.Normalize(mets).ToString(CultureInfo.InvariantCulture) : null);
                text.Field("Maximum heart rate", Unit(r.Tmt.MaxHeartRate, "bpm"));
                text.Field("Target heart rate reached", Unit(r.Tmt.TargetHeartRatePercent, "%"));
                text.Field("BP response", r.Tmt.BpResponse);
                text.Field("Symptoms", r.Tmt.Symptoms);
                text.Field("ST changes", r.Tmt.StChanges);
                text.Field("Reason for termination", r.Tmt.ReasonForTermination);
                text.Field("Result", r.Tmt.Result?.ToString());
                break;
            case CardiologyStudyType.Holter:
                text.Field("Duration", Unit(r.Holter.DurationHours, "h"));
                text.Field("Heart rate (min/mean/max)", r.Holter.MinHeartRate is null && r.Holter.MeanHeartRate is null && r.Holter.MaxHeartRate is null
                    ? null
                    : $"{r.Holter.MinHeartRate?.ToString(CultureInfo.InvariantCulture) ?? "-"}/{r.Holter.MeanHeartRate?.ToString(CultureInfo.InvariantCulture) ?? "-"}/{r.Holter.MaxHeartRate?.ToString(CultureInfo.InvariantCulture) ?? "-"} bpm");
                text.Field("Ventricular ectopics", r.Holter.VentricularEctopics?.ToString(CultureInfo.InvariantCulture));
                text.Field("Supraventricular ectopics", r.Holter.SupraventricularEctopics?.ToString(CultureInfo.InvariantCulture));
                text.Field("Pauses", r.Holter.Pauses?.ToString(CultureInfo.InvariantCulture));
                text.Field("Longest pause", r.Holter.LongestPause);
                text.Field("Arrhythmias", r.Holter.Arrhythmias);
                text.Field("ST changes", r.Holter.StChanges);
                break;
            case CardiologyStudyType.Abpm:
                text.Field("Daytime mean BP", Bp(r.Abpm.DaytimeMeanSystolic, r.Abpm.DaytimeMeanDiastolic));
                text.Field("Night-time mean BP", Bp(r.Abpm.NighttimeMeanSystolic, r.Abpm.NighttimeMeanDiastolic));
                text.Field("Dipping pattern", r.Abpm.DippingPattern);
                break;
        }

        text.Field("Conclusion", r.Conclusion);
        text.Field("Recommendations", r.Recommendations);
        if (r.IsCriticalFinding)
        {
            text.Line("Critical finding: communicated to the treating clinician.");
        }

        return text.ToString();
    }

    /// <summary>A UTF-8 plain-text rendering of a report, embedded in the resource.</summary>
    public static R4.Attachment TextAttachment(string text, string title, DateTimeOffset? created)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new R4.Attachment
        {
            ContentType = "text/plain; charset=utf-8",
            Language = "en",
            Data = bytes,
            SizeUnsignedInt = bytes.Length,
            Title = title,
            Creation = created is { } at ? FhirTime.DateTime(at) : null,
        };
    }

    private static R4.CodeableConcept Section(string code, string display) => FhirElements.Concept(FhirSystems.DiagnosticServiceSection, code, display);

    private static R4.Coding ServiceCoding(string serviceCode, string studyName) => FhirElements.Coding(FhirSystems.Service, serviceCode, studyName);

    private static R4.Identifier Accession(string number) => FhirElements.Identifier(FhirSystems.AccessionNumber, number, "ACSN", "Accession ID");

    private static string? Unit(int? value, string unit) => value is { } v ? $"{v.ToString(CultureInfo.InvariantCulture)} {unit}" : null;

    private static string? Bp(int? systolic, int? diastolic) =>
        systolic is null && diastolic is null ? null
        : $"{systolic?.ToString(CultureInfo.InvariantCulture) ?? "-"}/{diastolic?.ToString(CultureInfo.InvariantCulture) ?? "-"} mmHg";
}

/// <summary>Builds the plain-text renderings of reports: "Label: value" lines, skipping empty values.</summary>
internal sealed class ReportText
{
    private readonly StringBuilder _text = new();

    public void Line(string? value)
    {
        if (FhirElements.Clean(value) is { } line)
        {
            _text.Append(line).Append('\n');
        }
    }

    public void Field(string label, string? value)
    {
        if (FhirElements.Clean(value) is { } text)
        {
            _text.Append(label).Append(": ").Append(text).Append('\n');
        }
    }

    public override string ToString() => _text.ToString().TrimEnd('\n');
}
