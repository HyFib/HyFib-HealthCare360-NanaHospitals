using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Features.Incidents;

/// <summary>Any staff member's report. With <see cref="IsAnonymous"/> no reporter identity is stored.</summary>
public sealed record ReportIncidentRequest(
    IncidentType Type,
    IncidentSeverity Severity,
    DateTimeOffset OccurredAt,
    string Location,
    Guid? DepartmentId,
    Guid? PatientId,
    IReadOnlyList<Guid>? InvolvedStaffUserIds,
    string Description,
    string? ImmediateAction,
    string? Witnesses,
    SentinelEventKind? SentinelKind,
    bool FlagAsSentinel,
    bool IsAnonymous);

/// <summary>What the reporter gets back (anonymous reporters can quote the number to the quality team).</summary>
public sealed record IncidentReceipt(Guid Id, string IncidentNumber, bool IsSentinel, IncidentSeverity Severity, IncidentType Type);

public sealed record IncidentListItem(
    Guid Id,
    string IncidentNumber,
    IncidentType Type,
    IncidentSeverity Severity,
    bool IsSentinel,
    IncidentStatus Status,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReportedAt,
    string Location,
    Guid? DepartmentId,
    string? DepartmentName,
    bool IsAnonymous,
    Guid? AssignedToUserId,
    int OpenCapas);

public sealed record MyIncidentItem(Guid Id, string IncidentNumber, IncidentType Type, IncidentSeverity Severity, IncidentStatus Status, DateTimeOffset ReportedAt);

public sealed record PersonRef(Guid Id, string Name);

public sealed record IncidentCapaItem(Guid Id, string CapaNumber, CapaType Type, string Action, CapaStatus Status, DateOnly DueDate, bool IsOverdue);

public sealed record IncidentDetail(
    Guid Id,
    string IncidentNumber,
    IncidentType Type,
    IncidentSeverity Severity,
    bool IsSentinel,
    SentinelEventKind? SentinelKind,
    string? SentinelReason,
    IncidentStatus Status,
    IReadOnlyList<IncidentStatus> NextStatuses,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReportedAt,
    string Location,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? PatientId,
    IReadOnlyList<PersonRef> InvolvedStaff,
    string Description,
    string? ImmediateAction,
    string? Witnesses,
    bool IsAnonymous,
    string? ReporterName,
    PersonRef? AssignedTo,
    DateTimeOffset? ClosedAt,
    string? ClosureNote,
    string? LastStatusNote,
    int ReopenCount,
    InvestigationStatus? InvestigationStatus,
    IReadOnlyList<IncidentCapaItem> Capas,
    uint Version);

/// <summary>Re-grading after review (type, harm grade, sentinel classification, location details).</summary>
public sealed record UpdateIncidentRequest(
    IncidentType Type,
    IncidentSeverity Severity,
    SentinelEventKind? SentinelKind,
    bool FlagAsSentinel,
    string Location,
    Guid? DepartmentId,
    Guid? PatientId,
    IReadOnlyList<Guid>? InvolvedStaffUserIds,
    uint Version);

public sealed record TransitionIncidentRequest(IncidentStatus Status, string? Note);

public sealed record AssignIncidentRequest(Guid UserId);

public sealed record RcaFactorInput(ContributingFactorCategory Category, string Description, bool IsRootCause);

public sealed record SaveInvestigationRequest(
    RcaMethod Method,
    string ProblemStatement,
    IReadOnlyList<string>? Whys,
    IReadOnlyList<RcaFactorInput>? Factors,
    string? Findings,
    string? Recommendations,
    IReadOnlyList<Guid>? InvestigatorUserIds);

public sealed record RcaFactorDto(Guid Id, ContributingFactorCategory Category, string Description, bool IsRootCause);

public sealed record InvestigationDto(
    Guid Id,
    Guid IncidentId,
    RcaMethod Method,
    string ProblemStatement,
    IReadOnlyList<string> Whys,
    IReadOnlyList<RcaFactorDto> Factors,
    string? Findings,
    string? Recommendations,
    IReadOnlyList<PersonRef> Investigators,
    InvestigationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    uint Version);

public sealed class ReportIncidentRequestValidator : AbstractValidator<ReportIncidentRequest>
{
    public ReportIncidentRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.OccurredAt)
            .Must(at => at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The incident cannot be in the future.")
            .Must(at => at >= time.GetUtcNow().AddYears(-1)).WithMessage("Report incidents within a year of occurrence.");
        RuleFor(x => x.Location).NotEmpty().SafeText(150);
        RuleFor(x => x.Description).NotEmpty().MinimumLength(20).WithMessage("Describe what happened (at least 20 characters).").SafeText(4000);
        RuleFor(x => x.ImmediateAction).SafeText(2000);
        RuleFor(x => x.Witnesses).SafeText(500);
        RuleFor(x => x.InvolvedStaffUserIds).Must(ids => ids is null || ids.Count <= 20).WithMessage("At most 20 staff members.");
    }
}

public sealed class UpdateIncidentRequestValidator : AbstractValidator<UpdateIncidentRequest>
{
    public UpdateIncidentRequestValidator()
    {
        RuleFor(x => x.Location).NotEmpty().SafeText(150);
        RuleFor(x => x.InvolvedStaffUserIds).Must(ids => ids is null || ids.Count <= 20).WithMessage("At most 20 staff members.");
    }
}

public sealed class TransitionIncidentRequestValidator : AbstractValidator<TransitionIncidentRequest>
{
    public TransitionIncidentRequestValidator() => RuleFor(x => x.Note).SafeText(2000);
}

public sealed class SaveInvestigationRequestValidator : AbstractValidator<SaveInvestigationRequest>
{
    public SaveInvestigationRequestValidator()
    {
        RuleFor(x => x.ProblemStatement).NotEmpty().SafeText(2000);
        RuleFor(x => x.Whys).Must(w => w is null || w.Count <= 10).WithMessage("At most 10 'why' steps.");
        RuleForEach(x => x.Whys).NotEmpty().SafeText(500);
        RuleFor(x => x.Factors).Must(f => f is null || f.Count <= 50).WithMessage("At most 50 contributing factors.");
        RuleForEach(x => x.Factors).ChildRules(factor => factor.RuleFor(f => f.Description).NotEmpty().SafeText(500));
        RuleFor(x => x.Findings).SafeText(4000);
        RuleFor(x => x.Recommendations).SafeText(4000);
        RuleFor(x => x.InvestigatorUserIds).Must(ids => ids is null || ids.Count <= 10).WithMessage("At most 10 investigators.");
    }
}
