namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;

public enum TmtResult
{
    Negative = 0,
    Positive,
    Inconclusive,
}

/// <summary>12-lead ECG measurements (intervals in milliseconds).</summary>
public sealed record EcgFindings
{
    public int? HeartRate { get; init; }
    public string? Rhythm { get; init; }
    public int? PrInterval { get; init; }
    public int? QrsDuration { get; init; }
    public int? QtInterval { get; init; }
    public int? QtcInterval { get; init; }

    /// <summary>Frontal plane axis, for example "Normal (+60°)" or "Left axis deviation".</summary>
    public string? Axis { get; init; }

    public string? StTChanges { get; init; }
    public string? Interpretation { get; init; }
}

/// <summary>2D echocardiography with colour Doppler.</summary>
public sealed record EchoFindings
{
    /// <summary>Left ventricular ejection fraction, %.</summary>
    public decimal? Lvef { get; init; }

    public string? LeftVentricle { get; init; }
    public string? RightVentricle { get; init; }
    public string? LeftAtrium { get; init; }
    public string? RightAtrium { get; init; }
    public string? Valves { get; init; }

    /// <summary>Regional wall motion abnormality.</summary>
    public string? Rwma { get; init; }

    public string? DiastolicFunction { get; init; }
    public string? Pericardium { get; init; }
    public string? Conclusion { get; init; }
}

/// <summary>Treadmill (exercise stress) test.</summary>
public sealed record TmtFindings
{
    /// <summary>Protocol used (Bruce, modified Bruce).</summary>
    public string? Protocol { get; init; }

    public int? DurationSeconds { get; init; }
    public decimal? Mets { get; init; }
    public int? MaxHeartRate { get; init; }

    /// <summary>Maximum heart rate reached as a percentage of the age-predicted target.</summary>
    public int? TargetHeartRatePercent { get; init; }

    public string? BpResponse { get; init; }
    public string? Symptoms { get; init; }
    public string? StChanges { get; init; }
    public string? ReasonForTermination { get; init; }
    public TmtResult? Result { get; init; }
}

/// <summary>24–48 hour Holter monitoring.</summary>
public sealed record HolterFindings
{
    public int? DurationHours { get; init; }
    public int? MinHeartRate { get; init; }
    public int? MeanHeartRate { get; init; }
    public int? MaxHeartRate { get; init; }
    public int? VentricularEctopics { get; init; }
    public int? SupraventricularEctopics { get; init; }
    public int? Pauses { get; init; }
    public string? LongestPause { get; init; }
    public string? Arrhythmias { get; init; }
    public string? StChanges { get; init; }
}

/// <summary>24 hour ambulatory blood pressure monitoring (mmHg).</summary>
public sealed record AbpmFindings
{
    public int? DaytimeMeanSystolic { get; init; }
    public int? DaytimeMeanDiastolic { get; init; }
    public int? NighttimeMeanSystolic { get; init; }
    public int? NighttimeMeanDiastolic { get; init; }

    /// <summary>Dipper / non-dipper / reverse dipper.</summary>
    public string? DippingPattern { get; init; }
}

/// <summary>Structured content saved by the technician or cardiologist.</summary>
public sealed record CardiologyReportContent(
    EcgFindings? Ecg,
    EchoFindings? Echo,
    TmtFindings? Tmt,
    HolterFindings? Holter,
    AbpmFindings? Abpm,
    string? Conclusion,
    string? Recommendations,
    bool IsCriticalFinding,
    string? CriticalFindingNote);

/// <summary>
/// Structured cardiology report (CAR-03/04). The measurements for the study type are stored as columns (not free
/// text), so trends such as LVEF over time and QTc monitoring can be queried. The cardiologist's sign-off makes
/// the report final. A critical finding (for example an acute STEMI pattern) needs a documented call before sign-off.
/// </summary>
public sealed class CardiologyReport : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid CardiologyStudyId { get; set; }
    public Guid PatientId { get; set; }
    public CardiologyStudyType StudyType { get; set; }
    public EcgFindings Ecg { get; set; } = new();
    public EchoFindings Echo { get; set; } = new();
    public TmtFindings Tmt { get; set; } = new();
    public HolterFindings Holter { get; set; } = new();
    public AbpmFindings Abpm { get; set; } = new();
    public string? Conclusion { get; set; }
    public string? Recommendations { get; set; }
    public bool IsCriticalFinding { get; set; }
    public string? CriticalFindingNote { get; set; }
    public DiagnosticReportStatus Status { get; set; }
    public Guid? ReportedBy { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? VerificationCode { get; set; }

    public Result Save(CardiologyReportContent content)
    {
        if (Status is DiagnosticReportStatus.Verified or DiagnosticReportStatus.Released)
        {
            return Error.Rule("cardiology.report_signed", "The report is signed and cannot be edited.");
        }

        if (content.IsCriticalFinding && string.IsNullOrWhiteSpace(content.CriticalFindingNote))
        {
            return Error.Field("criticalFindingNote", "Describe the critical finding.");
        }

        Ecg = content.Ecg ?? Ecg;
        Echo = content.Echo ?? Echo;
        Tmt = content.Tmt ?? Tmt;
        Holter = content.Holter ?? Holter;
        Abpm = content.Abpm ?? Abpm;
        Conclusion = Clean(content.Conclusion);
        Recommendations = Clean(content.Recommendations);
        IsCriticalFinding = content.IsCriticalFinding;
        CriticalFindingNote = content.IsCriticalFinding ? Clean(content.CriticalFindingNote) : null;
        return Result.Success();
    }

    public Result Submit(Guid? by, DateTimeOffset now)
    {
        if (Status != DiagnosticReportStatus.Draft)
        {
            return Error.Rule("cardiology.report_submitted", "The report was already submitted.");
        }

        if (string.IsNullOrWhiteSpace(Conclusion))
        {
            return Error.Field("conclusion", "Enter the conclusion before submitting the report.");
        }

        Status = DiagnosticReportStatus.Reported;
        ReportedBy = by;
        ReportedAt = now;
        return Result.Success();
    }

    public Result Verify(Guid? by, DateTimeOffset now)
    {
        if (Status is DiagnosticReportStatus.Verified or DiagnosticReportStatus.Released)
        {
            return Error.Rule("cardiology.report_signed", "The report is already signed.");
        }

        if (string.IsNullOrWhiteSpace(Conclusion))
        {
            return Error.Field("conclusion", "Enter the conclusion before signing the report.");
        }

        ReportedBy ??= by;
        ReportedAt ??= now;
        Status = DiagnosticReportStatus.Verified;
        VerifiedBy = by;
        VerifiedAt = now;
        return Result.Success();
    }

    public Result Release(CardiologyStudy study, Guid? by, DateTimeOffset now)
    {
        if (Status != DiagnosticReportStatus.Verified)
        {
            return Error.Rule("cardiology.report_not_verified", "Sign off the report before releasing it.");
        }

        Status = DiagnosticReportStatus.Released;
        ReleasedBy = by;
        ReleasedAt = now;
        VerificationCode ??= Domain.VerificationCode.New();
        study.MarkReleased(now);
        Raise(new DiagnosticReportReleased(
            Id, DiagnosticKind.Cardiology, PatientId, study.VisitId, study.SourceOrderItemId, study.StudyName, IsCriticalFinding, now));
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
