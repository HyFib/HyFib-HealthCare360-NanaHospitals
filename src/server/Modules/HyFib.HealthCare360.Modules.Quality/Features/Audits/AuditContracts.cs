using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Audits;

namespace HyFib.HealthCare360.Modules.Quality.Features.Audits;

public sealed record AuditTemplateDto(Guid Id, string Code, string Title, NabhChapter Chapter, string? Description, IReadOnlyList<AuditItem> Items, bool IsActive, uint Version);

public sealed record AuditItemInput(string? Key, string Text, string? Reference);

public sealed record SaveAuditTemplateRequest(string Code, string Title, NabhChapter Chapter, string? Description, IReadOnlyList<AuditItemInput> Items, bool IsActive, uint? Version);

public sealed record AuditListItem(
    Guid Id,
    string AuditNumber,
    string Title,
    NabhChapter Chapter,
    string? Area,
    DateOnly ScheduledOn,
    Guid LeadAuditorUserId,
    string? LeadAuditorName,
    AuditRecurrence Recurrence,
    AuditStatus Status,
    bool IsOverdue,
    decimal? CompliancePercent,
    int NonCompliances,
    int OpenCapas);

public sealed record AuditFindingDto(
    Guid Id,
    int Order,
    string ItemKey,
    string ItemText,
    string? Reference,
    FindingResult? Result,
    string? Observation,
    Guid? CapaId,
    string? CapaNumber,
    CapaStatus? CapaStatus,
    string? CapaOwnerName,
    DateOnly? CapaDueDate);

public sealed record AuditDetail(
    Guid Id,
    string AuditNumber,
    Guid TemplateId,
    string Title,
    NabhChapter Chapter,
    Guid? DepartmentId,
    string? Area,
    DateOnly ScheduledOn,
    Guid LeadAuditorUserId,
    string? LeadAuditorName,
    AuditRecurrence Recurrence,
    AuditStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ClosedAt,
    string? Summary,
    string? CancelReason,
    decimal? CompliancePercent,
    IReadOnlyList<string> CompletionGaps,
    IReadOnlyList<AuditFindingDto> Findings,
    Guid? NextAuditId,
    bool CanRecord,
    uint Version);

public sealed record ScheduleAuditRequest(Guid TemplateId, DateOnly ScheduledOn, Guid LeadAuditorUserId, Guid? DepartmentId, string? Area, AuditRecurrence Recurrence);

public sealed record RecordFindingRequest(string ItemKey, FindingResult Result, string? Observation, uint Version);

/// <summary>Raises the CAPA for a non-compliance: what will be done, by whom and by when.</summary>
public sealed record RaiseFindingCapaRequest(CapaType Type, string Action, Guid OwnerUserId, DateOnly DueDate, uint Version);

public sealed record CompleteAuditRequest(string? Summary, uint Version);

public sealed record AuditStepRequest(uint Version);

public sealed record CancelAuditRequest(string Reason, uint Version);

public sealed class SaveAuditTemplateRequestValidator : AbstractValidator<SaveAuditTemplateRequest>
{
    public SaveAuditTemplateRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Must(c => c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            .WithMessage("Use letters, digits, '-' or '_'.");
        RuleFor(x => x.Title).NotEmpty().SafeText(200);
        RuleFor(x => x.Chapter).IsInEnum();
        RuleFor(x => x.Description).SafeText(1000);
        RuleFor(x => x.Items).NotEmpty().WithMessage("Give the checklist at least one item.").Must(i => i.Count <= 100).WithMessage("At most 100 items.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Key).SafeText(32);
            item.RuleFor(i => i.Text).NotEmpty().SafeText(500);
            item.RuleFor(i => i.Reference).SafeText(32);
        });
    }
}

public sealed class ScheduleAuditRequestValidator : AbstractValidator<ScheduleAuditRequest>
{
    public ScheduleAuditRequestValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.LeadAuditorUserId).NotEmpty();
        RuleFor(x => x.DepartmentId).OptionalId();
        RuleFor(x => x.Area).SafeText(150);
        RuleFor(x => x.Recurrence).IsInEnum();
    }
}

public sealed class RecordFindingRequestValidator : AbstractValidator<RecordFindingRequest>
{
    public RecordFindingRequestValidator()
    {
        RuleFor(x => x.ItemKey).NotEmpty().MaximumLength(32);
        RuleFor(x => x.Result).IsInEnum();
        RuleFor(x => x.Observation).SafeText(2000);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class RaiseFindingCapaRequestValidator : AbstractValidator<RaiseFindingCapaRequest>
{
    public RaiseFindingCapaRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Action).NotEmpty().MinimumLength(10).SafeText(2000);
        RuleFor(x => x.OwnerUserId).NotEmpty();
        RuleFor(x => x.DueDate).Must(d => d >= Services.QualityClock.LocalToday(time)).WithMessage("The due date cannot be in the past.");
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class CompleteAuditRequestValidator : AbstractValidator<CompleteAuditRequest>
{
    public CompleteAuditRequestValidator()
    {
        RuleFor(x => x.Summary).SafeText(2000);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class AuditStepRequestValidator : AbstractValidator<AuditStepRequest>
{
    public AuditStepRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed class CancelAuditRequestValidator : AbstractValidator<CancelAuditRequest>
{
    public CancelAuditRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}
