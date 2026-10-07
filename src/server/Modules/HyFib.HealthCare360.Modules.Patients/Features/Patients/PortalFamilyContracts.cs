using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

// ------------------------------------------------------------------ front desk

/// <summary>
/// Shares the patient's records with a family member's portal account, at the front desk. <paramref name="Relation"/> is
/// what the patient is to the family member; <paramref name="ConsentGivenBy"/> is the patient, or their guardian.
/// </summary>
public sealed record LinkFamilyMemberRequest(string AccountPhone, FamilyRelation Relation, string ConsentGivenBy, bool IdentityChecked);

public sealed record FamilyLinkCreated(Guid Id);

public sealed record RevokeFamilyLinkRequest(string Reason);

public sealed record PortalFamilyLinkDto(
    Guid Id,
    string AccountPhone,
    FamilyRelation Relation,
    FamilyLinkSource Source,
    DateTimeOffset LinkedAt,
    string? LinkedByName,
    string? ConsentGivenBy,
    string? CodeSentTo,
    DateTimeOffset? RevokedAt,
    string? RevokedByName,
    string? RevokedReason,
    bool IsActive);

/// <summary>Who can see the patient's records in the portal: the patient's own number, and the family links.</summary>
public sealed record PatientPortalAccessDto(string? OwnPhone, IReadOnlyList<PortalFamilyLinkDto> Links);

// ------------------------------------------------------------------ portal

/// <summary>Asks for a code sent to the family member's own mobile number; <paramref name="Relation"/> is what they are to you.</summary>
public sealed record RequestFamilyCodeRequest(string Uhid, string Phone, FamilyRelation Relation);

/// <summary>The same answer whether or not a patient matched, so the portal never tells whether a UHID and number match.</summary>
public sealed record FamilyCodeSent(Guid CodeId, DateTimeOffset ExpiresAt);

public sealed record VerifyFamilyCodeRequest(string Code);

/// <summary>A family member on another number whose records this account sees.</summary>
public sealed record PortalLinkedRelativeDto(
    Guid LinkId,
    Guid PatientId,
    string FullName,
    string Uhid,
    FamilyRelation Relation,
    FamilyLinkSource Source,
    DateTimeOffset LinkedAt);

/// <summary>Someone else's account that sees the records of a patient on this account's own number.</summary>
public sealed record PortalSharingDto(
    Guid LinkId,
    Guid PatientId,
    string PatientName,
    string AccountPhoneMasked,
    FamilyRelation Relation,
    DateTimeOffset LinkedAt);

public sealed record PortalFamilyDto(IReadOnlyList<PortalLinkedRelativeDto> Linked, IReadOnlyList<PortalSharingDto> SharedWith);

public sealed class LinkFamilyMemberRequestValidator : AbstractValidator<LinkFamilyMemberRequest>
{
    public LinkFamilyMemberRequestValidator()
    {
        RuleFor(x => x.AccountPhone).Must(phone => Phone.IsIndianMobile(Phone.Normalize(phone)))
            .WithMessage("Enter the family member's 10-digit mobile number.");
        RuleFor(x => x.Relation).IsInEnum();
        RuleFor(x => x.ConsentGivenBy).NotEmpty().SafeText(200)
            .WithMessage("Say who agreed: the patient, or their guardian by name.");
        RuleFor(x => x.IdentityChecked).Equal(true)
            .WithMessage("Check who the patient and the family member are before sharing the records.");
    }
}

public sealed class RevokeFamilyLinkRequestValidator : AbstractValidator<RevokeFamilyLinkRequest>
{
    public RevokeFamilyLinkRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class RequestFamilyCodeRequestValidator : AbstractValidator<RequestFamilyCodeRequest>
{
    public RequestFamilyCodeRequestValidator()
    {
        RuleFor(x => x.Uhid).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9-]+$")
            .WithMessage("Enter the UHID printed on the hospital card or a bill.");
        RuleFor(x => x.Phone).Must(phone => Phone.IsIndianMobile(Phone.Normalize(phone)))
            .WithMessage("Enter a valid 10-digit mobile number.");
        RuleFor(x => x.Relation).IsInEnum();
    }
}

public sealed class VerifyFamilyCodeRequestValidator : AbstractValidator<VerifyFamilyCodeRequest>
{
    public VerifyFamilyCodeRequestValidator() => RuleFor(x => x.Code).NotEmpty().MaximumLength(10);
}
