using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Breaches;

public sealed record BreachListItem(
    Guid Id,
    string BreachNumber,
    string Title,
    BreachStatus Status,
    DateTimeOffset AwareAt,
    DateTimeOffset ReportDueAt,
    bool ReportOverdue,
    int? EstimatedAffected,
    int AffectedListed,
    int Notified);

public sealed record BreachDetail(
    Guid Id,
    string BreachNumber,
    string Title,
    string Description,
    BreachStatus Status,
    DateTimeOffset AwareAt,
    DateTimeOffset? OccurredFrom,
    DateTimeOffset? OccurredTo,
    IReadOnlyList<BreachNature> Nature,
    string? Location,
    IReadOnlyList<BreachDataCategory> DataCategories,
    int? EstimatedAffected,
    string? Consequences,
    string? Mitigation,
    string? PrincipalSteps,
    string? Cause,
    string? Remediation,
    string? PrincipalNotice,
    DateTimeOffset? BoardInformedAt,
    DateTimeOffset ReportDueAt,
    DateTimeOffset? ExtendedUntil,
    DateTimeOffset? ReportSubmittedAt,
    string? BoardReference,
    DateTimeOffset? ClosedAt,
    string? ClosingNote,
    DateTimeOffset? NotifiedAt,
    bool ReportOverdue,
    int AffectedListed,
    int Notified,
    int WithoutContact,
    IReadOnlyList<string> MissingForReport,
    IReadOnlyList<string> MissingForNotice,
    uint Version);

/// <summary>A new breach, or changes to one. <see cref="Version"/> is required for changes.</summary>
public sealed record SaveBreachRequest(
    string Title,
    string Description,
    DateTimeOffset AwareAt,
    DateTimeOffset? OccurredFrom,
    DateTimeOffset? OccurredTo,
    IReadOnlyList<BreachNature>? Nature,
    string? Location,
    IReadOnlyList<BreachDataCategory>? DataCategories,
    int? EstimatedAffected,
    string? Consequences,
    string? Mitigation,
    string? PrincipalSteps,
    string? Cause,
    string? Remediation,
    string? PrincipalNotice,
    uint? Version);

public sealed record BoardInformedRequest(DateTimeOffset At, string? Reference);

public sealed record BoardExtensionRequest(DateTimeOffset Until, string? Reference);

public sealed record ReportSubmittedRequest(DateTimeOffset At, string? Reference);

public sealed record CloseBreachRequest(string? Note);

/// <summary>Patients to add to the breach: chosen one by one (ids), or pasted as a list of UHIDs.</summary>
public sealed record AddAffectedRequest(IReadOnlyList<Guid>? PatientIds, IReadOnlyList<string>? Uhids);

public sealed record AddAffectedResult(int Added, int AlreadyListed, IReadOnlyList<string> NotFound);

public sealed record AffectedPatientItem(
    Guid PatientId,
    string Uhid,
    string FullName,
    string? Phone,
    bool HasEmail,
    DateTimeOffset AddedAt,
    DateTimeOffset? NotifiedAt,
    string? NoticeChannels);

/// <summary>What happened when the notices were sent: queued, not reachable (no mobile or e-mail), already sent before.</summary>
public sealed record NotifyAffectedResult(int Queued, int WithoutContact, int AlreadyNotified);

/// <summary>Everything the detailed report to the Data Protection Board covers (DPDP Rules 2025, rule 7(2)), for printing.</summary>
public sealed record BreachReport(
    string Hospital,
    string? LegalName,
    string? Address,
    string? RegistrationNumber,
    PrivacyContact Contact,
    BreachDetail Breach,
    DateTimeOffset GeneratedAt);

public sealed class SaveBreachRequestValidator : AbstractValidator<SaveBreachRequest>
{
    public SaveBreachRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("Give the breach a short title.").SafeText(200);
        RuleFor(x => x.Description).NotEmpty().WithMessage("Describe what happened.").SafeText(4000);
        RuleFor(x => x.OccurredTo).GreaterThanOrEqualTo(x => x.OccurredFrom!.Value).When(x => x.OccurredFrom is not null && x.OccurredTo is not null)
            .WithMessage("The end must be after the start.");
        RuleForEach(x => x.Nature).IsInEnum();
        RuleForEach(x => x.DataCategories).IsInEnum();
        RuleFor(x => x.EstimatedAffected).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Location).SafeText(500);
        RuleFor(x => x.Consequences).SafeText(4000);
        RuleFor(x => x.Mitigation).SafeText(4000);
        RuleFor(x => x.PrincipalSteps).SafeText(4000);
        RuleFor(x => x.Cause).SafeText(4000);
        RuleFor(x => x.Remediation).SafeText(4000);
        RuleFor(x => x.PrincipalNotice).SafeText(320);
    }
}

public sealed class BoardInformedRequestValidator : AbstractValidator<BoardInformedRequest>
{
    public BoardInformedRequestValidator() => RuleFor(x => x.Reference).SafeText(100);
}

public sealed class BoardExtensionRequestValidator : AbstractValidator<BoardExtensionRequest>
{
    public BoardExtensionRequestValidator() => RuleFor(x => x.Reference).SafeText(100);
}

public sealed class ReportSubmittedRequestValidator : AbstractValidator<ReportSubmittedRequest>
{
    public ReportSubmittedRequestValidator() => RuleFor(x => x.Reference).SafeText(100);
}

public sealed class CloseBreachRequestValidator : AbstractValidator<CloseBreachRequest>
{
    public CloseBreachRequestValidator() => RuleFor(x => x.Note).SafeText(1000);
}

public sealed class AddAffectedRequestValidator : AbstractValidator<AddAffectedRequest>
{
    public AddAffectedRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.PatientIds?.Count ?? 0) + (x.Uhids?.Count ?? 0) > 0)
            .OverridePropertyName("uhids").WithMessage("Choose patients, or paste their UHIDs.");
        RuleFor(x => x.PatientIds).Must(ids => ids is null || ids.Count <= 5000).WithMessage("Add at most 5,000 patients at a time.");
        RuleFor(x => x.Uhids).Must(u => u is null || u.Count <= 5000).WithMessage("Paste at most 5,000 UHIDs at a time.");
        RuleForEach(x => x.Uhids).NotEmpty().SafeText(32);
    }
}
