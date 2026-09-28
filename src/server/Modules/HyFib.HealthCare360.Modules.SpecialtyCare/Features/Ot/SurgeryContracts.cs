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
    string? Notes);

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
    uint Version);

public sealed record ScheduleSurgeryRequest(Guid TheatreId, DateTimeOffset ScheduledStart, DateTimeOffset ScheduledEnd, uint Version);

public sealed record SurgeryReasonRequest(string Reason);

public sealed record SurgeryCreated(Guid Id, string BookingNumber, SurgeryStatus Status);

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
    ChargeStatus ChargeStatus);

public sealed record ChecklistItemDto(
    string Key, string Text, bool AllowsNotApplicable, ChecklistItemState State, string? Remarks, string? RecordedBy, DateTimeOffset? RecordedAt);

public sealed record SafetyPhaseDto(SafetyPhase Phase, bool IsComplete, DateTimeOffset? CompletedAt, IReadOnlyList<ChecklistItemDto> Items);

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
    uint Version);

public sealed class CreateSurgeryBookingRequestValidator : AbstractValidator<CreateSurgeryBookingRequest>
{
    public CreateSurgeryBookingRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ProcedureName).NotEmpty().WithMessage("Enter the planned procedure.").SafeText(300);
        RuleFor(x => x.ProcedureCode).MaximumLength(32);
        RuleFor(x => x.Diagnosis).SafeText(500);
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
