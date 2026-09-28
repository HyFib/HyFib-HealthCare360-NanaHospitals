using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Features.FollowUps;

public sealed record FollowUpListItem(
    Guid Id,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? PhoneMasked,
    FollowUpType Type,
    DateOnly DueDate,
    FollowUpStatus Status,
    FollowUpPriority Priority,
    Guid? AssigneeUserId,
    int Attempts,
    FollowUpOutcome? Outcome,
    bool IsOverdue);

/// <summary>Task detail with the patient's full phone number (viewing it is audited as PHI access).</summary>
public sealed record FollowUpDetail(
    Guid Id,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? Phone,
    Guid? VisitId,
    Guid? AdmissionId,
    FollowUpType Type,
    DateOnly DueDate,
    FollowUpStatus Status,
    FollowUpPriority Priority,
    Guid? AssigneeUserId,
    string? Notes,
    int Attempts,
    FollowUpOutcome? Outcome,
    string? OutcomeNotes,
    DateTimeOffset? CompletedAt,
    FollowUpSource Source,
    bool IsOverdue,
    uint Version);

public sealed record CreateFollowUpRequest(
    Guid PatientId,
    Guid? VisitId,
    FollowUpType Type,
    DateOnly DueDate,
    Guid? AssigneeUserId,
    FollowUpPriority Priority,
    string? Notes);

public sealed record UpdateFollowUpRequest(DateOnly DueDate, Guid? AssigneeUserId, FollowUpPriority Priority, string? Notes, uint Version);

public sealed record FollowUpAttemptRequest(FollowUpOutcome Outcome, string? Notes, DateOnly? NextDueDate);

public sealed record CancelFollowUpRequest(string Reason);

public sealed class CreateFollowUpRequestValidator : AbstractValidator<CreateFollowUpRequest>
{
    public CreateFollowUpRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class UpdateFollowUpRequestValidator : AbstractValidator<UpdateFollowUpRequest>
{
    public UpdateFollowUpRequestValidator() => RuleFor(x => x.Notes).SafeText(1000);
}

public sealed class FollowUpAttemptRequestValidator : AbstractValidator<FollowUpAttemptRequest>
{
    public FollowUpAttemptRequestValidator() => RuleFor(x => x.Notes).SafeText(1000);
}

public sealed class CancelFollowUpRequestValidator : AbstractValidator<CancelFollowUpRequest>
{
    public CancelFollowUpRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}
