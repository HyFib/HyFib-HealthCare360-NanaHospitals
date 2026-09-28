using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Insurance;

public sealed record PayerListItem(
    Guid Id,
    string Code,
    string Name,
    PayerCategory Category,
    string? ContactPerson,
    string? Phone,
    Guid? TariffPlanId,
    string? TariffPlanName,
    int CreditDays,
    bool IsActive);

public sealed record PayerDetail(
    Guid Id,
    string Code,
    string Name,
    PayerCategory Category,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Address,
    string? Gstin,
    Guid? TariffPlanId,
    int CreditDays,
    decimal DefaultCoPayPercent,
    string? PortalUrl,
    bool IsActive,
    string? Notes,
    uint Version);

public sealed record SavePayerRequest(
    string Code,
    string Name,
    PayerCategory Category,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Address,
    string? Gstin,
    Guid? TariffPlanId,
    int CreditDays,
    decimal DefaultCoPayPercent,
    string? PortalUrl,
    bool IsActive,
    string? Notes,
    uint? Version);

public sealed record NonPayableItemDto(
    Guid Id,
    Guid? PayerId,
    string? PayerName,
    Guid? ServiceItemId,
    string? ServiceCode,
    string? ServiceName,
    ServiceCategory? Category,
    string? Notes);

public sealed record SaveNonPayableItemRequest(Guid? PayerId, Guid? ServiceItemId, ServiceCategory? Category, string? Notes);

/// <summary>A policy as shown in lists and forms: the policy number is always masked.</summary>
public sealed record PolicyDto(
    Guid Id,
    Guid PatientId,
    Guid PayerId,
    string? PayerName,
    Guid? TpaId,
    string? TpaName,
    string PolicyNumberMasked,
    string? MemberId,
    string? PolicyHolderName,
    string? RelationToHolder,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    decimal SumInsured,
    decimal CoPayPercent,
    bool IsActive,
    bool IsValidToday,
    DateTimeOffset? VerifiedAt,
    string? VerificationNotes,
    Guid? CardFileId,
    string? Notes,
    uint Version);

/// <summary>Create (policy number required) or update (policy number <c>null</c> = unchanged) a patient policy.</summary>
public sealed record SavePolicyRequest(
    Guid PatientId,
    Guid PayerId,
    Guid? TpaId,
    string? PolicyNumber,
    string? MemberId,
    string? PolicyHolderName,
    string? RelationToHolder,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    decimal SumInsured,
    decimal CoPayPercent,
    bool IsActive,
    Guid? CardFileId,
    string? Notes,
    uint? Version);

public sealed record VerifyPolicyRequest(string? Notes);

public sealed record PolicyNumberDto(Guid PolicyId, string PolicyNumber);

public sealed class SavePayerRequestValidator : AbstractValidator<SavePayerRequest>
{
    public SavePayerRequestValidator()
    {
        RuleFor(x => x.Code).Code();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.ContactPerson).MaximumLength(200);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).SafeText(500);
        RuleFor(x => x.Gstin).Gstin().When(x => !string.IsNullOrWhiteSpace(x.Gstin));
        RuleFor(x => x.CreditDays).InclusiveBetween(0, 365);
        RuleFor(x => x.DefaultCoPayPercent).Percent();
        RuleFor(x => x.PortalUrl).MaximumLength(300)
            .Must(url => string.IsNullOrWhiteSpace(url) || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("Enter the portal's https:// address.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class SaveNonPayableItemRequestValidator : AbstractValidator<SaveNonPayableItemRequest>
{
    public SaveNonPayableItemRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.ServiceItemId is null) != (x.Category is null))
            .WithName("serviceItemId").WithMessage("Name either a service or a category.");
        RuleFor(x => x.Notes).SafeText(300);
    }
}

public sealed class SavePolicyRequestValidator : AbstractValidator<SavePolicyRequest>
{
    public SavePolicyRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.PayerId).NotEmpty();
        RuleFor(x => x.PolicyNumber).NotEmpty().When(x => x.Version is null).WithMessage("Enter the policy or card number.");
        RuleFor(x => x.PolicyNumber).MaximumLength(64)
            .Must(n => n is null || n.Trim().All(c => char.IsLetterOrDigit(c) || c is '-' or '/' or ' '))
            .WithMessage("Use letters, digits, spaces, dashes or slashes.");
        RuleFor(x => x.MemberId).MaximumLength(64);
        RuleFor(x => x.PolicyHolderName).MaximumLength(200);
        RuleFor(x => x.RelationToHolder).MaximumLength(64);
        RuleFor(x => x.ValidTo).GreaterThanOrEqualTo(x => x.ValidFrom).WithMessage("The policy must end after it starts.");
        RuleFor(x => x.SumInsured).NonNegativeAmount();
        RuleFor(x => x.CoPayPercent).Percent();
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class VerifyPolicyRequestValidator : AbstractValidator<VerifyPolicyRequest>
{
    public VerifyPolicyRequestValidator() => RuleFor(x => x.Notes).SafeText(500);
}
