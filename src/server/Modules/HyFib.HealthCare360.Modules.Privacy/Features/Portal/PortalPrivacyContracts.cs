using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Portal;

public sealed record PortalDataRequestItem(
    Guid Id,
    string RequestNumber,
    Guid PatientId,
    DataRequestType Type,
    DataRequestStatus Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ClosedAt);

/// <summary>
/// A request as the principal sees it: the public history only, never staff notes. <see cref="IdentityConfirmed"/>
/// false on a request made for someone else means the hospital still has to confirm the requester may act for them.
/// </summary>
public sealed record PortalDataRequestDetail(
    Guid Id,
    string RequestNumber,
    Guid PatientId,
    DataRequestType Type,
    DataRequestStatus Status,
    RequesterRelation RequestedBy,
    string? RequesterName,
    bool IdentityConfirmed,
    string Details,
    IReadOnlyList<PortalCorrectionItem> Corrections,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ClosedAt,
    string? Response,
    IReadOnlyList<string> ErasedItems,
    DateOnly? RetainedUntil,
    bool RetainedPermanently,
    IReadOnlyList<PortalRequestEvent> Timeline,
    bool CanWithdraw,
    PrivacyContact Contact);

/// <summary>
/// The privacy centre at a glance, for its front page: whether a nominee is named, how many requests are open and how
/// many the portal allows, and how many breach notices there are. Counts only, so opening the page is not record access.
/// </summary>
public sealed record PortalPrivacyStatus(bool HasNominee, string? NomineeName, int OpenRequests, int MaxOpenRequests, int Notices);

public sealed record PortalCorrectionItem(string Field, string? CurrentValue, string RequestedValue, CorrectionDecision Decision, string? Note);

public sealed record PortalRequestEvent(DateTimeOffset At, DataRequestEventKind Kind, string Text);

/// <summary>
/// A request from the portal. Access needs no request (the summary is always available there), so the portal sends
/// corrections, erasures and grievances, for the account holder or a family member on the same number.
/// </summary>
public sealed record PortalDataRequestCreate(
    Guid PatientId,
    DataRequestType Type,
    RequesterRelation RequestedBy,
    string? RequesterName,
    string? Details,
    IReadOnlyList<CorrectionInput>? Corrections);

public sealed record PortalRequestCreated(Guid Id, string RequestNumber, DateTimeOffset DueAt);

public sealed record PortalNomineeRequest(Guid PatientId, string Name, string Relationship, string? Phone, string? Email);

public sealed class PortalDataRequestCreateValidator : AbstractValidator<PortalDataRequestCreate>
{
    public PortalDataRequestCreateValidator()
    {
        RuleFor(x => x.Type).Must(t => t is DataRequestType.Correction or DataRequestType.Erasure or DataRequestType.Grievance)
            .WithMessage("Choose a correction, an erasure or a concern.");
        RuleFor(x => x.RequestedBy).Must(r => r is RequesterRelation.Self or RequesterRelation.ParentOrGuardian or RequesterRelation.FamilyMember)
            .WithMessage("Say whether the request is for you or for a family member.");
        RuleFor(x => x.RequesterName).NotEmpty().When(x => x.RequestedBy != RequesterRelation.Self).WithMessage("Enter your name.");
        RuleFor(x => x.RequesterName).SafeText(200);
        RuleFor(x => x.Details).NotEmpty().When(x => x.Type != DataRequestType.Correction || x.Corrections is not { Count: > 0 })
            .WithMessage("Tell us what you would like us to do.");
        RuleFor(x => x.Details).SafeText(4000);
        RuleFor(x => x.Corrections).Must(c => c is null || c.Count == 0).When(x => x.Type != DataRequestType.Correction)
            .WithMessage("Only a correction lists changes.");
        RuleFor(x => x.Corrections!).SetValidator(new CorrectionListValidator()).When(x => x.Corrections is not null);
    }
}

public sealed class PortalNomineeRequestValidator : AbstractValidator<PortalNomineeRequest>
{
    public PortalNomineeRequestValidator() => NomineeRules.Apply(this, x => x.Name, x => x.Relationship, x => x.Phone, x => x.Email);
}
