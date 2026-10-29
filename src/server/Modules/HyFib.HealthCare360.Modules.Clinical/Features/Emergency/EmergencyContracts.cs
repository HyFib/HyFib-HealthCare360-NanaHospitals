using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emergency;

/// <summary>A patient on the ER board. Times are in minutes from arrival; a restricted patient the user may not open shows initials.</summary>
public sealed record ErBoardRow(
    Guid CaseId,
    Guid VisitId,
    Guid PatientId,
    string PatientName,
    string Uhid,
    string? Gender,
    int? AgeYears,
    DateTimeOffset ArrivedAt,
    ArrivalMode ArrivalMode,
    int? Level,
    string? ChiefComplaint,
    int MinutesInDepartment,
    int? DoorToTriageMinutes,
    int? DoorToDoctorMinutes,
    string? DoctorName,
    bool IsMlc,
    string? MlcNumber,
    bool MlcIntimationOverdue,
    uint Version);

/// <summary>The department now: who is in it, most urgent first, and how many at each level.</summary>
/// <param name="ByLevel">Patients at levels 1 to 5, in that order.</param>
public sealed record ErBoard(IReadOnlyList<ErBoardRow> Rows, int Untriaged, IReadOnlyList<int> ByLevel);

public sealed record EsiTriageDto(
    Guid Id,
    DateTimeOffset AssessedAt,
    string AssessedByName,
    string ChiefComplaint,
    int ComputedLevel,
    int Level,
    IReadOnlyList<string> Reasons,
    string? OverrideReason,
    int? Pulse,
    int? RespiratoryRate,
    int? SpO2,
    int? SystolicBp,
    int? DiastolicBp,
    decimal? TemperatureCelsius,
    int? Gcs,
    int? PainScore);

public sealed record MlcDto(
    Guid Id,
    string Number,
    DateTimeOffset IdentifiedAt,
    MlcBroughtBy BroughtBy,
    string? BroughtByName,
    MlcCause Cause,
    DateTimeOffset? IncidentAt,
    string? IncidentPlace,
    string Injuries,
    bool? ConsciousOnArrival,
    bool? SmellOfAlcohol,
    string DoctorName,
    string? DoctorRegistrationNumber,
    string PoliceStation,
    DateTimeOffset? IntimatedAt,
    MlcIntimationMode? IntimationMode,
    string? ReceivedBy,
    string? PoliceEntryNumber,
    bool IntimationOverdue,
    uint Version,
    bool IsChild = false,
    bool IsPocso = false,
    PocsoReportedTo? ReportedTo = null,
    MlcExaminationDto? Examination = null,
    string? InvestigatingOfficer = null,
    DateTimeOffset? ReportForwardedAt = null,
    DateTimeOffset? ReportDueAt = null,
    bool ReportOverdue = false);

/// <summary>A sexual-assault victim's medical examination, as recorded (BNSS 2023, s.184; for a child, POCSO Act 2012, s.27).</summary>
public sealed record MlcExaminationDto(
    Guid ExaminerId,
    string ExaminerName,
    string? ExaminerRegistration,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string ConsentGivenBy,
    ExaminationAttendant? AttendantKind,
    string? AttendantName);

public sealed record EmergencyCaseDetail(
    Guid Id,
    Guid VisitId,
    Guid PatientId,
    string PatientName,
    string Uhid,
    string? Gender,
    int? AgeYears,
    DateTimeOffset ArrivedAt,
    ArrivalMode ArrivalMode,
    int? Level,
    DateTimeOffset? TriagedAt,
    DateTimeOffset? DoctorSeenAt,
    string? DoctorName,
    EmergencyDisposition? Disposition,
    DateTimeOffset? DisposedAt,
    string? DisposedByName,
    string? DispositionNotes,
    string? ReferredTo,
    IReadOnlyList<EsiTriageDto> Triages,
    MlcDto? Mlc,
    uint Version);

/// <summary>A triage: the answers at decision points A to C, the measured vitals, and the level if the nurse assigns another.</summary>
public sealed record TriageRequest(
    string ChiefComplaint,
    ArrivalMode ArrivalMode,
    bool NeedsLifeSavingIntervention,
    bool HighRiskSituation,
    bool ConfusedLethargicOrDisoriented,
    bool SevereDistress,
    int ExpectedResources,
    int? Pulse,
    int? RespiratoryRate,
    int? SpO2,
    int? SystolicBp,
    int? DiastolicBp,
    decimal? TemperatureCelsius,
    int? Gcs,
    int? PainScore,
    int? Level,
    string? OverrideReason,
    uint Version);

/// <summary>The answers and vitals typed so far, for the level the algorithm gives before the triage is saved.</summary>
public sealed record TriagePreviewRequest(
    bool NeedsLifeSavingIntervention,
    bool HighRiskSituation,
    bool ConfusedLethargicOrDisoriented,
    bool SevereDistress,
    int ExpectedResources,
    int? Pulse,
    int? RespiratoryRate,
    int? SpO2,
    int? SystolicBp,
    int? DiastolicBp,
    decimal? TemperatureCelsius,
    int? Gcs,
    int? PainScore);

/// <summary>The level the algorithm gives for the answers and vitals typed so far.</summary>
public sealed record TriagePreview(int Level, IReadOnlyList<string> Reasons, bool DangerZoneVitals);

public sealed record CaseVersionRequest(uint Version);

public sealed record DispositionRequest(EmergencyDisposition Disposition, string? Notes, string? ReferredTo, uint Version);

/// <summary>Opens or corrects the case's MLC entry. <paramref name="Version"/>: the entry's, when correcting one.</summary>
/// <param name="ChildVictim">
/// Whether the patient is under 18, said by the doctor for a sexual assault when the date of birth is not known; a date of
/// birth under 18 makes the patient a child whatever is said.
/// </param>
public sealed record SaveMlcRequest(
    MlcBroughtBy BroughtBy,
    string? BroughtByName,
    MlcCause Cause,
    DateTimeOffset? IncidentAt,
    string? IncidentPlace,
    string Injuries,
    bool? ConsciousOnArrival,
    bool? SmellOfAlcohol,
    string PoliceStation,
    string? DoctorRegistrationNumber,
    uint? Version,
    bool? ChildVictim = null);

/// <param name="ReportedTo">For a sexual offence against a child: the Special Juvenile Police Unit or the local police (POCSO s.19(1)).</param>
public sealed record MlcIntimationRequest(
    DateTimeOffset IntimatedAt, MlcIntimationMode Mode, string ReceivedBy, string? PoliceEntryNumber, uint Version, PocsoReportedTo? ReportedTo = null);

/// <summary>The medical examination of a victim of sexual assault, by the examining doctor in the practitioner master.</summary>
/// <param name="ConsentGivenBy">Who consented: the patient, or a person competent to consent for them, with their relation.</param>
/// <param name="AttendantKind">For a child: who was present (POCSO s.27(3)–(4)).</param>
public sealed record MlcExaminationRequest(
    Guid ExaminerId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string ConsentGivenBy, ExaminationAttendant? AttendantKind,
    string? AttendantName, uint Version);

/// <summary>The examination's report forwarded to the investigating officer (BNSS s.184(6)).</summary>
public sealed record MlcReportRequest(DateTimeOffset ForwardedAt, string InvestigatingOfficer, uint Version);

public sealed record MlcRegisterRow(
    Guid Id,
    Guid CaseId,
    string Number,
    DateTimeOffset IdentifiedAt,
    string PatientName,
    string Uhid,
    MlcCause Cause,
    string PoliceStation,
    DateTimeOffset? IntimatedAt,
    string? ReceivedBy,
    bool IntimationOverdue,
    string DoctorName,
    bool IsPocso = false,
    DateTimeOffset? ReportDueAt = null,
    DateTimeOffset? ReportForwardedAt = null,
    bool ReportOverdue = false);

public sealed class TriageRequestValidator : AbstractValidator<TriageRequest>
{
    public TriageRequestValidator()
    {
        RuleFor(x => x.ChiefComplaint).NotEmpty().SafeText(300);
        RuleFor(x => x.ArrivalMode).IsInEnum();
        RuleFor(x => x.ExpectedResources).InclusiveBetween(0, 2);
        RuleFor(x => x.Pulse).InclusiveBetween(0, 300);
        RuleFor(x => x.RespiratoryRate).InclusiveBetween(0, 100);
        RuleFor(x => x.SpO2).InclusiveBetween(0, 100);
        RuleFor(x => x.SystolicBp).InclusiveBetween(0, 300);
        RuleFor(x => x.DiastolicBp).InclusiveBetween(0, 250);
        RuleFor(x => x.TemperatureCelsius).InclusiveBetween(25m, 45m);
        RuleFor(x => x.Gcs).InclusiveBetween(3, 15);
        RuleFor(x => x.PainScore).InclusiveBetween(0, 10);
        RuleFor(x => x.Level).InclusiveBetween(1, 5);
        RuleFor(x => x.OverrideReason).SafeText(300);
    }
}

public sealed class TriagePreviewRequestValidator : AbstractValidator<TriagePreviewRequest>
{
    public TriagePreviewRequestValidator()
    {
        RuleFor(x => x.ExpectedResources).InclusiveBetween(0, 2);
    }
}

public sealed class DispositionRequestValidator : AbstractValidator<DispositionRequest>
{
    public DispositionRequestValidator()
    {
        RuleFor(x => x.Disposition).IsInEnum();
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.ReferredTo).SafeText(200);
    }
}

public sealed class SaveMlcRequestValidator : AbstractValidator<SaveMlcRequest>
{
    public SaveMlcRequestValidator()
    {
        RuleFor(x => x.BroughtBy).IsInEnum();
        RuleFor(x => x.BroughtByName).SafeText(200);
        RuleFor(x => x.Cause).IsInEnum();
        RuleFor(x => x.IncidentPlace).SafeText(300);
        RuleFor(x => x.Injuries).NotEmpty().SafeText(4000);
        RuleFor(x => x.PoliceStation).NotEmpty().SafeText(200);
        RuleFor(x => x.DoctorRegistrationNumber).SafeText(64);
    }
}

public sealed class MlcIntimationRequestValidator : AbstractValidator<MlcIntimationRequest>
{
    public MlcIntimationRequestValidator()
    {
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.ReceivedBy).NotEmpty().SafeText(200);
        RuleFor(x => x.PoliceEntryNumber).SafeText(64);
        RuleFor(x => x.ReportedTo).IsInEnum();
    }
}

public sealed class MlcExaminationRequestValidator : AbstractValidator<MlcExaminationRequest>
{
    public MlcExaminationRequestValidator()
    {
        RuleFor(x => x.ExaminerId).NotEmpty();
        RuleFor(x => x.ConsentGivenBy).NotEmpty().SafeText(200);
        RuleFor(x => x.AttendantKind).IsInEnum();
        RuleFor(x => x.AttendantName).SafeText(200);
    }
}

public sealed class MlcReportRequestValidator : AbstractValidator<MlcReportRequest>
{
    public MlcReportRequestValidator()
    {
        RuleFor(x => x.InvestigatingOfficer).NotEmpty().SafeText(200);
    }
}

public sealed class CaseVersionRequestValidator : AbstractValidator<CaseVersionRequest>;
