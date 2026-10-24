using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Physio;

public sealed record CreatePhysioReferralRequest(
    Guid PatientId, Guid? VisitId, Guid? AdmissionId, Guid? ReferredByPractitionerId, OrderPriority Priority, string Reason, int? SessionsOrdered = null);

public sealed record PhysioReasonRequest(string Reason);

public sealed record RecordPhysioAssessmentRequest(
    PhysioAssessmentType Type,
    string ChiefComplaint,
    int? PainScore,
    string? PainLocation,
    IReadOnlyList<RangeOfMotion>? RangeOfMotion,
    IReadOnlyList<MuscleStrength>? Strength,
    string? FunctionalStatus,
    string? Goals,
    string? Notes);

/// <summary><see cref="SessionsPlanned"/> may be left empty to plan the number of sessions prescribed on the order.</summary>
public sealed record SaveTreatmentPlanRequest(
    IReadOnlyList<PhysioModality> Modalities,
    string? ExerciseProgramme,
    int? SessionsPlanned,
    int SessionsPerWeek,
    int SessionMinutes,
    DateOnly StartDate,
    string? Precautions,
    string? Goals,
    uint? Version);

/// <summary>Books <see cref="Count"/> sessions from <see cref="FirstSessionAt"/>, spaced by the plan frequency unless an interval is given.</summary>
public sealed record SchedulePhysioSessionsRequest(DateTimeOffset FirstSessionAt, int Count, int? IntervalDays);

public sealed record ReschedulePhysioSessionRequest(DateTimeOffset ScheduledAt, uint Version);

public sealed record AttendPhysioSessionRequest(
    IReadOnlyList<PhysioModality> Modalities, string ProgressNote, int? PainBefore, int? PainAfter, int? DurationMinutes);

/// <summary><see cref="Missed"/> = the patient did not come; otherwise the department cancelled the session.</summary>
public sealed record ClosePhysioSessionRequest(string Reason, bool Missed);

public sealed record PhysioReferralListItem(
    Guid Id, Guid PatientId, string Uhid, string PatientName, Gender Gender, string? Age, string? ServiceName, string? Reason, int? SessionsOrdered,
    OrderPriority Priority, PhysioReferralStatus Status, DateTimeOffset ReferredAt, Guid? ReferredByPractitionerId, string? ReferredByName, string? AssignedTherapistName,
    bool IsInpatient);

public sealed record PhysioAssessmentDto(
    Guid Id, PhysioAssessmentType Type, DateTimeOffset AssessedAt, string? AssessedByName, string ChiefComplaint, int? PainScore, string? PainLocation,
    IReadOnlyList<RangeOfMotion> RangeOfMotion, IReadOnlyList<MuscleStrength> Strength, string? FunctionalStatus, string? Goals, string? Notes);

public sealed record TreatmentPlanDto(
    Guid Id, IReadOnlyList<PhysioModality> Modalities, string? ExerciseProgramme, int SessionsPlanned, int SessionsPerWeek, int SessionMinutes,
    DateOnly StartDate, string? Precautions, string? Goals, int Revision, uint Version);

public sealed record PhysioSessionDto(
    Guid Id, Guid ReferralId, Guid PatientId, int SessionNumber, DateTimeOffset ScheduledAt, PhysioSessionStatus Status,
    IReadOnlyList<PhysioModality> ModalitiesGiven, string? ProgressNote, int? PainBefore, int? PainAfter, int? DurationMinutes, DateTimeOffset? AttendedAt,
    string? TherapistName, string? MissedReason, ChargeStatus ChargeStatus, string? ChargeError, uint Version);

public sealed record PhysioReferralDetail(
    Guid Id, PatientSummary? Patient, Guid PatientId, Guid? VisitId, Guid? AdmissionId, Guid? OrderItemId, string? OrderNumber, string? ServiceName,
    int? SessionsOrdered, Guid? ReferredByPractitionerId, string? ReferredByName, OrderPriority Priority, string? Reason, DateTimeOffset ReferredAt, PhysioReferralStatus Status,
    string? AssignedTherapistName, string? DischargeSummary, DateTimeOffset? ClosedAt, string? CancelReason, IReadOnlyList<PhysioAssessmentDto> Assessments,
    TreatmentPlanDto? Plan, IReadOnlyList<PhysioSessionDto> Sessions, uint Version);

/// <summary>Row of the physiotherapy day list.</summary>
public sealed record PhysioWorklistItem(
    Guid SessionId, Guid ReferralId, Guid PatientId, string Uhid, string PatientName, int SessionNumber, int? SessionsPlanned, DateTimeOffset ScheduledAt,
    PhysioSessionStatus Status, string? TherapistName, uint Version);

public sealed class CreatePhysioReferralRequestValidator : AbstractValidator<CreatePhysioReferralRequest>
{
    public CreatePhysioReferralRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Describe the problem and what physiotherapy should address.").SafeText(1000);
        RuleFor(x => x.SessionsOrdered).InclusiveBetween(1, 100);
    }
}

public sealed class PhysioReasonRequestValidator : AbstractValidator<PhysioReasonRequest>
{
    public PhysioReasonRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(2000);
}

public sealed class RecordPhysioAssessmentRequestValidator : AbstractValidator<RecordPhysioAssessmentRequest>
{
    public RecordPhysioAssessmentRequestValidator()
    {
        RuleFor(x => x.ChiefComplaint).NotEmpty().SafeText(1000);
        RuleFor(x => x.PainScore).PainScore();
        RuleFor(x => x.PainLocation).SafeText(200);
        RuleFor(x => x.FunctionalStatus).SafeText(2000);
        RuleFor(x => x.Goals).SafeText(2000);
        RuleFor(x => x.Notes).SafeText(2000);
        RuleFor(x => x.RangeOfMotion).Must(r => r is null || r.Count <= 60).WithMessage("Too many range-of-motion entries.");
        RuleForEach(x => x.RangeOfMotion).ChildRules(r =>
        {
            r.RuleFor(v => v.Joint).NotEmpty().MaximumLength(50);
            r.RuleFor(v => v.Movement).NotEmpty().MaximumLength(50);
            r.RuleFor(v => v.ActiveDegrees).InclusiveBetween(0, 360);
            r.RuleFor(v => v.PassiveDegrees).InclusiveBetween(0, 360);
        });
        RuleFor(x => x.Strength).Must(s => s is null || s.Count <= 60).WithMessage("Too many strength entries.");
        RuleForEach(x => x.Strength).ChildRules(s =>
        {
            s.RuleFor(v => v.MuscleGroup).NotEmpty().MaximumLength(50);
            s.RuleFor(v => v.MrcGrade).InclusiveBetween(0, 5).WithMessage("MRC grade must be between 0 and 5.");
        });
    }
}

public sealed class SaveTreatmentPlanRequestValidator : AbstractValidator<SaveTreatmentPlanRequest>
{
    public SaveTreatmentPlanRequestValidator()
    {
        RuleFor(x => x.Modalities).NotEmpty().WithMessage("Choose at least one modality.");
        RuleFor(x => x.ExerciseProgramme).SafeText(4000);
        RuleFor(x => x.SessionsPlanned).InclusiveBetween(1, 100);
        RuleFor(x => x.SessionsPerWeek).InclusiveBetween(1, 14);
        RuleFor(x => x.SessionMinutes).InclusiveBetween(10, 180);
        RuleFor(x => x.Precautions).SafeText(1000);
        RuleFor(x => x.Goals).SafeText(2000);
    }
}

public sealed class SchedulePhysioSessionsRequestValidator : AbstractValidator<SchedulePhysioSessionsRequest>
{
    public SchedulePhysioSessionsRequestValidator()
    {
        RuleFor(x => x.Count).InclusiveBetween(1, 30);
        RuleFor(x => x.IntervalDays).InclusiveBetween(1, 30);
    }
}

public sealed class ReschedulePhysioSessionRequestValidator : AbstractValidator<ReschedulePhysioSessionRequest>
{
    public ReschedulePhysioSessionRequestValidator(TimeProvider time) =>
        RuleFor(x => x.ScheduledAt).Must(at => at >= time.GetUtcNow().AddHours(-12)).WithMessage("Sessions cannot be moved into the past.");
}

public sealed class AttendPhysioSessionRequestValidator : AbstractValidator<AttendPhysioSessionRequest>
{
    public AttendPhysioSessionRequestValidator()
    {
        RuleFor(x => x.Modalities).NotEmpty().WithMessage("Select the modalities given in this session.");
        RuleFor(x => x.ProgressNote).NotEmpty().WithMessage("Write a progress note for this session.").SafeText(4000);
        RuleFor(x => x.PainBefore).PainScore();
        RuleFor(x => x.PainAfter).PainScore();
        RuleFor(x => x.DurationMinutes).InclusiveBetween(1, 240);
    }
}

public sealed class ClosePhysioSessionRequestValidator : AbstractValidator<ClosePhysioSessionRequest>
{
    public ClosePhysioSessionRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(500);
}
