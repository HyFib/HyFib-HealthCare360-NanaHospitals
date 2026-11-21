using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

public sealed record CreateSurgeryBookingRequest(
    Guid PatientId,
    Guid? AdmissionId,
    Guid? VisitId,
    Guid? ProcedureServiceItemId,
    string? ProcedureCode,
    string ProcedureName,
    Laterality Laterality,
    string? Diagnosis,
    Guid? SurgeonId,
    IReadOnlyList<Guid>? AssistantIds,
    Guid? AnaesthetistId,
    AnaesthesiaType AnaesthesiaType,
    SurgeryPriority Priority,
    DateOnly? PreferredDate,
    Guid? TheatreId,
    DateTimeOffset? ScheduledStart,
    DateTimeOffset? ScheduledEnd,
    string? Notes,
    Guid? NurseId = null,
    string? SiteLevel = null);

/// <param name="ChangeReason">
/// Why the side, level or procedure changes after the WHO Sign-in: required then, when only the operating surgeon or the
/// anaesthetist may change it, and the Sign-in (and Time-out) are redone.
/// </param>
public sealed record UpdateSurgeryPlanRequest(
    Guid? ProcedureServiceItemId,
    string? ProcedureCode,
    string ProcedureName,
    Laterality Laterality,
    string? Diagnosis,
    Guid? SurgeonId,
    IReadOnlyList<Guid>? AssistantIds,
    Guid? AnaesthetistId,
    AnaesthesiaType AnaesthesiaType,
    SurgeryPriority Priority,
    DateOnly? PreferredDate,
    string? Notes,
    uint Version,
    Guid? NurseId = null,
    string? SiteLevel = null,
    string? ChangeReason = null);

public sealed record ScheduleSurgeryRequest(Guid TheatreId, DateTimeOffset ScheduledStart, DateTimeOffset ScheduledEnd, uint Version);

public sealed record SurgeryReasonRequest(string Reason);

/// <summary>
/// An emergency operation going ahead without the patient's consent (the emergency doctrine): the operating surgeon's
/// clinical reason it cannot wait (at least 10 characters), and the confirmation of the second doctor who agrees, given at
/// the surgeon's screen with their own user name and password (<c>POST /api/v1/auth/witness</c>, purpose
/// <c>ot.consent_waiver</c>, the patient).
/// </summary>
public sealed record WaiveSurgicalConsentRequest(string Reason, Guid ConcurringDoctorConfirmationId);

/// <summary>
/// The patient's consent to the operation as the theatre's gates read it: each consent needed (to the procedure, and to
/// anaesthesia unless it is local or topical) with where it stands, counted from <paramref name="Since"/> (the booking), who
/// recorded it and whether the signed form is attached; the emergency waiver, if any; and the consents the incision relied on.
/// Each check carries, nullable, the patient's latest refusal of that consent recorded before the booking
/// (<see cref="EarlierRefusal"/>) while nothing of that kind is recorded since: it is shown, and audited with a waiver, but
/// blocks nothing.
/// </summary>
/// <param name="Satisfied">
/// The gates open: every consent needed is recorded, or consent was waived in an emergency, the booking is still one and
/// the patient has not said no since (refused, or withdrawn a consent).
/// </param>
/// <param name="Recorded">Every consent needed is recorded.</param>
/// <param name="Waiver">
/// The emergency waiver on the booking; it stands only while the booking is an emergency and the patient has not said no.
/// </param>
/// <param name="Refused">The patient's latest word since the booking on a consent the operation needs is a refusal.</param>
/// <param name="SaidNo">
/// The patient's latest word since the booking on a consent the operation needs is "no": a refusal (<paramref name="Refused"/>)
/// or a withdrawal. No emergency waiver is given or stands against it.
/// </param>
/// <param name="AnaesthesiaConsentRequiredAtSignIn">
/// The WHO Sign-in was done under anaesthesia given by an anaesthetist, so the consent to anaesthesia stays needed though
/// the technique is now local or topical.
/// </param>
public sealed record SurgicalConsentDto(
    bool Satisfied,
    bool Recorded,
    DateTimeOffset Since,
    IReadOnlyList<SurgicalConsentCheck> Checks,
    SurgicalConsentWaiver? Waiver,
    Guid? ProcedureConsentId,
    Guid? AnaesthesiaConsentId,
    bool Refused = false,
    bool AnaesthesiaConsentRequiredAtSignIn = false,
    bool SaidNo = false);

public sealed record SurgeryCreated(Guid Id, string BookingNumber, SurgeryStatus Status);

/// <param name="InRecovery">Between the WHO Sign-out and the discharge from recovery.</param>
public sealed record SurgeryListItem(
    Guid Id,
    string BookingNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string ProcedureName,
    Laterality Laterality,
    SurgeryPriority Priority,
    SurgeryStatus Status,
    Guid? TheatreId,
    string? TheatreCode,
    DateTimeOffset? ScheduledStart,
    DateTimeOffset? ScheduledEnd,
    DateOnly? PreferredDate,
    Guid? SurgeonId,
    string? SurgeonName,
    AnaesthesiaType AnaesthesiaType,
    bool SignInDone,
    bool TimeOutDone,
    bool SignOutDone,
    ChargeStatus ChargeStatus,
    bool InRecovery = false);

public sealed record ChecklistItemDto(
    string Key, string Text, bool AllowsNotApplicable, ChecklistItemState State, string? Remarks, string? RecordedBy, DateTimeOffset? RecordedAt);

public sealed record SafetyPhaseDto(SafetyPhase Phase, bool IsComplete, DateTimeOffset? CompletedAt, IReadOnlyList<ChecklistItemDto> Items);

/// <param name="InRecovery">Between the WHO Sign-out and the discharge from recovery: shown as "In recovery".</param>
/// <param name="RecoveryDischargedAt">When the patient left the recovery room, for <paramref name="RecoveryDestination"/>.</param>
public sealed record SurgeryDetail(
    Guid Id,
    string BookingNumber,
    PatientSummary? Patient,
    Guid PatientId,
    Guid? AdmissionId,
    Guid? VisitId,
    Guid? OrderItemId,
    Guid? ProcedureServiceItemId,
    string? ProcedureCode,
    string ProcedureName,
    Laterality Laterality,
    string? Diagnosis,
    Guid? SurgeonId,
    string? SurgeonName,
    IReadOnlyList<Guid> AssistantIds,
    Guid? AnaesthetistId,
    string? AnaesthetistName,
    AnaesthesiaType AnaesthesiaType,
    SurgeryPriority Priority,
    DateOnly? PreferredDate,
    Guid? TheatreId,
    string? TheatreName,
    DateTimeOffset? ScheduledStart,
    DateTimeOffset? ScheduledEnd,
    SurgeryStatus Status,
    DateTimeOffset? ReceivedInPreOpAt,
    DateTimeOffset? InRoomAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool PreOpComplete,
    IReadOnlyList<ChecklistItemDto> PreOpChecklist,
    IReadOnlyList<SafetyPhaseDto> SafetyChecklist,
    string? Notes,
    string? CancelReason,
    string? PostponeReason,
    int PostponeCount,
    ChargeStatus ChargeStatus,
    string? ChargeError,
    uint Version,
    Guid? NurseId = null,
    string? NurseName = null,
    string? SiteLevel = null,
    bool SiteMarkingRequired = false,
    IReadOnlyList<SurgicalCount>? Counts = null,
    IReadOnlyList<CountTally>? CountTallies = null,
    IReadOnlyList<CountDiscrepancy>? CountDiscrepancies = null,
    IReadOnlyList<ChecklistVoid>? ChecklistVoids = null,
    bool InRecovery = false,
    DateTimeOffset? RecoveryDischargedAt = null,
    RecoveryDestination? RecoveryDestination = null,
    SurgicalConsentDto? Consent = null);

public sealed class CreateSurgeryBookingRequestValidator : AbstractValidator<CreateSurgeryBookingRequest>
{
    public CreateSurgeryBookingRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ProcedureName).NotEmpty().WithMessage("Enter the planned procedure.").SafeText(300);
        RuleFor(x => x.ProcedureCode).MaximumLength(32);
        RuleFor(x => x.Diagnosis).SafeText(500);
        RuleFor(x => x.SiteLevel).SafeText(60);
        RuleFor(x => x.Notes).SafeText(2000);
        RuleFor(x => x.AssistantIds).Must(a => a is null || a.Count <= 10).WithMessage("Add at most 10 assistants.");
        RuleFor(x => x)
            .Must(x => (x.TheatreId is null && x.ScheduledStart is null && x.ScheduledEnd is null)
                       || (x.TheatreId is not null && x.ScheduledStart is not null && x.ScheduledEnd is not null))
            .WithName("scheduledStart").WithMessage("To schedule now, give the theatre, start and end together (or leave all three empty for a request).");
        RuleFor(x => x.SurgeonId).NotEmpty().When(x => x.TheatreId is not null).WithMessage("Choose the operating surgeon.");
    }
}

public sealed class UpdateSurgeryPlanRequestValidator : AbstractValidator<UpdateSurgeryPlanRequest>
{
    public UpdateSurgeryPlanRequestValidator()
    {
        RuleFor(x => x.ProcedureName).NotEmpty().SafeText(300);
        RuleFor(x => x.ProcedureCode).MaximumLength(32);
        RuleFor(x => x.Diagnosis).SafeText(500);
        RuleFor(x => x.SiteLevel).SafeText(60);
        RuleFor(x => x.ChangeReason).SafeText(500);
        RuleFor(x => x.Notes).SafeText(2000);
        RuleFor(x => x.AssistantIds).Must(a => a is null || a.Count <= 10).WithMessage("Add at most 10 assistants.");
    }
}

public sealed class ScheduleSurgeryRequestValidator : AbstractValidator<ScheduleSurgeryRequest>
{
    public ScheduleSurgeryRequestValidator()
    {
        RuleFor(x => x.TheatreId).NotEmpty();
        RuleFor(x => x.ScheduledEnd).GreaterThan(x => x.ScheduledStart).WithMessage("The end time must be after the start time.");
    }
}

public sealed class SurgeryReasonRequestValidator : AbstractValidator<SurgeryReasonRequest>
{
    public SurgeryReasonRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Give a reason.").SafeText(500);
}

public sealed class WaiveSurgicalConsentRequestValidator : AbstractValidator<WaiveSurgicalConsentRequest>
{
    public WaiveSurgicalConsentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage(ReasonMessage).SafeText(500);
        RuleFor(x => x.Reason).Must(r => r!.Trim().Length >= SurgicalConsent.MinimumReasonLength).When(x => !string.IsNullOrWhiteSpace(x.Reason))
            .WithMessage(ReasonMessage);
        RuleFor(x => x.ConcurringDoctorConfirmationId).NotEmpty()
            .WithMessage("A second doctor agrees that the operation cannot wait, confirming here with their own user name and password.");
    }

    internal const string ReasonMessage = "Give the clinical reason the operation cannot wait for the patient's consent (at least 10 characters).";
}
