using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Requests;

public sealed record DataRequestListItem(
    Guid Id,
    string RequestNumber,
    DataRequestType Type,
    DataRequestStatus Status,
    DataRequestChannel Channel,
    Guid? PatientId,
    string? Uhid,
    string PrincipalName,
    RequesterRelation RequestedBy,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    bool IsOverdue,
    bool IdentityVerified,
    Guid? AssignedToUserId,
    string? AssignedToName,
    DateTimeOffset? ClosedAt);

/// <summary>A member of staff a request can be given to: holds <c>privacy.requests.manage</c> in the facility.</summary>
public sealed record PrivacyOfficer(Guid Id, string DisplayName);

public sealed record DataRequestDetail(
    Guid Id,
    string RequestNumber,
    DataRequestType Type,
    DataRequestStatus Status,
    DataRequestChannel Channel,
    Guid? PatientId,
    string? Uhid,
    string PrincipalName,
    string? ContactPhone,
    string? ContactEmail,
    RequesterRelation RequestedBy,
    string? RequesterName,
    string Details,
    IReadOnlyList<CorrectionItemDto> Corrections,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    bool IsOverdue,
    IdentityCheck? IdentityCheck,
    string? IdentityNote,
    DateTimeOffset? IdentityVerifiedAt,
    string? IdentityVerifiedBy,
    Guid? AssignedToUserId,
    string? AssignedToName,
    string? Response,
    DateTimeOffset? ClosedAt,
    string? ClosedBy,
    IReadOnlyList<string> ErasedItems,
    DateOnly? RetainedUntil,
    bool RetainedPermanently,
    bool HasSummary,
    IReadOnlyList<DataRequestEventDto> Events,
    uint Version);

/// <summary>A requested change, by its position in the request (<see cref="Index"/> is what decisions refer to).</summary>
public sealed record CorrectionItemDto(
    int Index,
    string Field,
    string FieldLabel,
    string? CurrentValue,
    string RequestedValue,
    CorrectionDecision Decision,
    string? Note);

public sealed record DataRequestEventDto(DateTimeOffset At, DataRequestEventKind Kind, string Text, string? By, bool IsPublic);

public sealed record CorrectionInput(string Field, string RequestedValue);

/// <summary>A request recorded by staff: at the desk, or received by e-mail, letter or phone.</summary>
public sealed record CreateDataRequestRequest(
    DataRequestType Type,
    DataRequestChannel Channel,
    Guid? PatientId,
    string? PrincipalName,
    string? ContactPhone,
    string? ContactEmail,
    RequesterRelation RequestedBy,
    string? RequesterName,
    string? Details,
    IReadOnlyList<CorrectionInput>? Corrections,
    IdentityCheck? IdentityCheck,
    string? IdentityNote);

public sealed record VerifyIdentityRequest(IdentityCheck Check, string? Note);

public sealed record AssignDataRequestRequest(Guid? UserId);

public sealed record DataRequestNoteRequest(string Text);

public sealed record CorrectionDecisionInput(int Index, CorrectionDecision Decision, string? Note);

public sealed record DecideCorrectionsRequest(IReadOnlyList<CorrectionDecisionInput> Items);

public sealed record CloseDataRequestRequest(DataRequestStatus Outcome, string Response);

public sealed record EraseDataRequest(IReadOnlyList<string> Items);

/// <summary>The privacy workload at a glance (home widget and the queue's header).</summary>
public sealed record PrivacyOverview(int Open, int Overdue, int DueThisWeek, int AwaitingIdentityCheck, int OpenBreaches, int BreachReportsOverdue);

public sealed class CreateDataRequestRequestValidator : AbstractValidator<CreateDataRequestRequest>
{
    public CreateDataRequestRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Channel).IsInEnum().NotEqual(DataRequestChannel.Portal).WithMessage("Portal requests come from the patient portal.");
        RuleFor(x => x.RequestedBy).IsInEnum();
        RuleFor(x => x.PrincipalName).NotEmpty().When(x => x.PatientId is null)
            .WithMessage("Choose the patient, or enter the name of the person the request is about.");
        RuleFor(x => x.PrincipalName).SafeText(200);
        RuleFor(x => x.ContactPhone).PhoneNumber();
        RuleFor(x => x.ContactEmail).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.ContactEmail));
        RuleFor(x => x).Must(x => x.PatientId is not null || !string.IsNullOrWhiteSpace(x.ContactPhone) || !string.IsNullOrWhiteSpace(x.ContactEmail))
            .OverridePropertyName("contactPhone").WithMessage("Enter a phone number or e-mail address to reply to.");
        RuleFor(x => x.RequesterName).NotEmpty().When(x => x.RequestedBy != RequesterRelation.Self)
            .WithMessage("Enter the name of the person making the request.");
        RuleFor(x => x.RequesterName).SafeText(200);
        RuleFor(x => x.Details).NotEmpty().When(x => x.Type != DataRequestType.Correction || x.Corrections is not { Count: > 0 })
            .WithMessage("Describe the request.");
        RuleFor(x => x.Details).SafeText(4000);
        RuleFor(x => x.Corrections).Must(c => c is null || c.Count == 0).When(x => x.Type != DataRequestType.Correction)
            .WithMessage("Only a correction request lists changes.");
        RuleFor(x => x.Corrections!).SetValidator(new CorrectionListValidator()).When(x => x.Corrections is not null);
        RuleFor(x => x.IdentityCheck).IsInEnum().NotEqual(IdentityCheck.PortalSignIn).When(x => x.IdentityCheck is not null)
            .WithMessage("Choose how you checked the requester's identity.");
        RuleFor(x => x.IdentityNote).NotEmpty().When(x => x.IdentityCheck == IdentityCheck.Other).WithMessage("Say how the identity was checked.");
        RuleFor(x => x.IdentityNote).SafeText(500);
    }
}

/// <summary>At most 12 changes, each to a known detail, once.</summary>
public sealed class CorrectionListValidator : AbstractValidator<IReadOnlyList<CorrectionInput>>
{
    public CorrectionListValidator()
    {
        RuleFor(x => x).Must(c => c.Count <= 12).WithMessage("List at most 12 changes.");
        RuleFor(x => x).Must(c => c.Select(i => i.Field).Distinct(StringComparer.Ordinal).Count() == c.Count)
            .WithMessage("List each detail once.");
        RuleForEach(x => x).ChildRules(item =>
        {
            item.RuleFor(i => i.Field).Must(CorrectableFields.IsKnown).WithMessage("Choose a detail from the list.");
            item.RuleFor(i => i.RequestedValue).NotEmpty().WithMessage("Enter the correct value.").SafeText(500);
        });
    }
}

public sealed class VerifyIdentityRequestValidator : AbstractValidator<VerifyIdentityRequest>
{
    public VerifyIdentityRequestValidator()
    {
        RuleFor(x => x.Check).IsInEnum().NotEqual(IdentityCheck.PortalSignIn).WithMessage("Choose how you checked the requester's identity.");
        RuleFor(x => x.Note).NotEmpty().When(x => x.Check == IdentityCheck.Other).WithMessage("Say how the identity was checked.");
        RuleFor(x => x.Note).SafeText(500);
    }
}

public sealed class DataRequestNoteRequestValidator : AbstractValidator<DataRequestNoteRequest>
{
    public DataRequestNoteRequestValidator() => RuleFor(x => x.Text).NotEmpty().SafeText(2000);
}

public sealed class DecideCorrectionsRequestValidator : AbstractValidator<DecideCorrectionsRequest>
{
    public DecideCorrectionsRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().Must(i => i.Count <= 12);
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Decision).IsInEnum().NotEqual(CorrectionDecision.Pending).WithMessage("Accept or decline the change.");
            item.RuleFor(i => i.Note).SafeText(500);
        });
    }
}

public sealed class CloseDataRequestRequestValidator : AbstractValidator<CloseDataRequestRequest>
{
    public CloseDataRequestRequestValidator()
    {
        RuleFor(x => x.Outcome).Must(o => o is DataRequestStatus.Completed or DataRequestStatus.PartlyCompleted or DataRequestStatus.Declined)
            .WithMessage("Choose completed, partly completed or declined.");
        RuleFor(x => x.Response).NotEmpty().WithMessage("Write the reply.").SafeText(4000);
    }
}

public sealed class EraseDataRequestValidator : AbstractValidator<EraseDataRequest>
{
    public EraseDataRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Choose what to erase.");
        RuleForEach(x => x.Items).Must(ErasureService.IsKnownItem).WithMessage("Choose from the list of what can be erased.");
    }
}
