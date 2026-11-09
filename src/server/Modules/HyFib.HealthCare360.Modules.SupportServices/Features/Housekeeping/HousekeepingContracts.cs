using HyFib.HealthCare360.Modules.SupportServices.Domain.Housekeeping;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Housekeeping;

public sealed record CleaningAreaDto(
    Guid Id, string Code, string Name, Guid? WardId, string? WardName, CleaningRiskZone RiskZone, CleaningFrequency Frequency, int TimesPerDay,
    IReadOnlyList<DayOfWeek> WeekDays, IReadOnlyList<string> Checklist, bool IsActive, uint Version);

public sealed record SaveCleaningAreaRequest(
    string Code, string Name, Guid? WardId, CleaningRiskZone RiskZone, CleaningFrequency Frequency, int TimesPerDay, IReadOnlyList<DayOfWeek>? WeekDays,
    IReadOnlyList<string> Checklist, bool IsActive, uint? Version);

public sealed record CleaningRecordDto(
    Guid Id, Guid AreaId, DateOnly Date, int Slot, DateTimeOffset CleanedAt, string CleanedByName, IReadOnlyList<ChecklistResult> Checklist, string? Notes,
    CleaningStatus Status, DateTimeOffset? VerifiedAt, string? VerifiedByName, string? VerificationRemarks, bool CleanedByYou, uint Version);

/// <summary>One cleaning due on the day, and what was done about it: nothing yet, done and waiting, verified, or sent back.</summary>
public sealed record CleaningSlot(Guid AreaId, string AreaCode, string AreaName, string? WardName, CleaningRiskZone RiskZone, int Slot, IReadOnlyList<string> Checklist,
    CleaningRecordDto? Record);

/// <summary>The day's schedule: every cleaning due, with the count not done (missed, once the day is over) and waiting to be verified.</summary>
public sealed record CleaningBoard(DateOnly Date, IReadOnlyList<CleaningSlot> Slots, int Due, int NotDone, int AwaitingVerification, int Redo);

public sealed record RecordCleaningRequest(DateOnly Date, int Slot, IReadOnlyList<ChecklistResult> Checklist, string? Notes);

public sealed record VerifyCleaningRequest(bool Passed, string? Remarks, uint Version);

public sealed record RaiseCleaningRequest(CleaningRequestKind Kind, Guid? WardId, string Location, string? Details);

public sealed record CleaningRequestStepRequest(uint Version);

public sealed record CompleteCleaningRequest(IReadOnlyList<ChecklistResult> Checklist, string? Notes, uint Version);

public sealed record CancelCleaningRequest(string Reason, uint Version);

public sealed record CleaningRequestDto(
    Guid Id, CleaningRequestKind Kind, Guid? WardId, string? WardName, string Location, string? Details, DateTimeOffset RequestedAt, string RequestedByName,
    CleaningRequestStatus Status, int TargetMinutes, DateTimeOffset? AcknowledgedAt, string? AcknowledgedByName, int? ResponseMinutes, bool ResponseLate,
    DateTimeOffset? CompletedAt, string? CompletedByName, int? CompletionMinutes, IReadOnlyList<string> ChecklistItems, IReadOnlyList<ChecklistResult> Checklist,
    string? Notes, DateTimeOffset? VerifiedAt, string? VerifiedByName, string? VerificationRemarks, bool CompletedByYou, uint Version);

/// <summary>How housekeeping did over a period: requests by kind with their median response and how many were late, and the schedule kept.</summary>
public sealed record HousekeepingReport(
    DateOnly From, DateOnly To, IReadOnlyList<RequestKindSummary> Requests, int ScheduledDue, int ScheduledDone, int ScheduledVerified, int SentBack);

public sealed record RequestKindSummary(CleaningRequestKind Kind, int Requests, int TargetMinutes, double? MedianResponseMinutes, int Late, int SentBack);

internal static class ChecklistRules
{
    public static void Answers<T>(IRuleBuilder<T, IReadOnlyList<ChecklistResult>> rule) =>
        rule.NotNull().Must(l => l is null || l.Count <= 30).WithMessage("At most 30 checklist items.");
}

public sealed class SaveCleaningAreaRequestValidator : AbstractValidator<SaveCleaningAreaRequest>
{
    public SaveCleaningAreaRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits and hyphens for the code.");
        RuleFor(x => x.Name).NotEmpty().SafeText(120);
        RuleFor(x => x.RiskZone).IsInEnum();
        RuleFor(x => x.Frequency).IsInEnum();
        RuleFor(x => x.TimesPerDay).InclusiveBetween(1, CleaningArea.MaxTimesPerDay);
        RuleFor(x => x.WeekDays).Must(d => d is { Count: > 0 }).When(x => x.Frequency == CleaningFrequency.Weekly)
            .WithMessage("Choose the days of the week the area is cleaned.");
        RuleFor(x => x.Checklist).NotEmpty().WithMessage("Give the area a checklist.").Must(c => c is null || c.Count <= 30).WithMessage("At most 30 checklist items.");
        RuleForEach(x => x.Checklist).NotEmpty().SafeText(120);
    }
}

public sealed class RecordCleaningRequestValidator : AbstractValidator<RecordCleaningRequest>
{
    public RecordCleaningRequestValidator()
    {
        RuleFor(x => x.Slot).InclusiveBetween(1, CleaningArea.MaxTimesPerDay);
        ChecklistRules.Answers(RuleFor(x => x.Checklist));
        RuleForEach(x => x.Checklist).ChildRules(c =>
        {
            c.RuleFor(r => r.Item).NotEmpty().SafeText(120);
            c.RuleFor(r => r.Note).SafeText(200);
        });
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class VerifyCleaningRequestValidator : AbstractValidator<VerifyCleaningRequest>
{
    public VerifyCleaningRequestValidator()
    {
        RuleFor(x => x.Remarks).SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class RaiseCleaningRequestValidator : AbstractValidator<RaiseCleaningRequest>
{
    public RaiseCleaningRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Location).NotEmpty().SafeText(200);
        RuleFor(x => x.Details).SafeText(500);
    }
}

public sealed class CleaningRequestStepRequestValidator : AbstractValidator<CleaningRequestStepRequest>
{
    public CleaningRequestStepRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed class CompleteCleaningRequestValidator : AbstractValidator<CompleteCleaningRequest>
{
    public CompleteCleaningRequestValidator()
    {
        ChecklistRules.Answers(RuleFor(x => x.Checklist));
        RuleForEach(x => x.Checklist).ChildRules(c =>
        {
            c.RuleFor(r => r.Item).NotEmpty().SafeText(120);
            c.RuleFor(r => r.Note).SafeText(200);
        });
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class CancelCleaningRequestValidator : AbstractValidator<CancelCleaningRequest>
{
    public CancelCleaningRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
        RuleFor(x => x.Version).SeenVersion();
    }
}
