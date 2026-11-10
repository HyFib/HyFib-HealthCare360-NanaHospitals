using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

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
    uint Version,
    PendingPayerTerms? PendingTerms = null);

/// <summary>
/// A payer saved. Its category, tariff plan, co-pay and putting it in use change only when a second person approves them
/// (<paramref name="Reason"/> is for the approver); its other details, and taking it out of use, are saved at once.
/// </summary>
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
    uint? Version,
    string? Reason = null);

/// <summary>
/// An item of the non-payable list, or one asked to be added (<see cref="Waiting"/> says so, and it is not in force); an
/// item asked to be taken off is in force until approved.
/// </summary>
public sealed record NonPayableItemDto(
    Guid Id,
    Guid? PayerId,
    string? PayerName,
    Guid? ServiceItemId,
    string? ServiceCode,
    string? ServiceName,
    ServiceCategory? Category,
    string? Notes,
    PendingNonPayable? Waiting = null);

/// <summary>An item to add to the non-payable list, with <paramref name="Reason"/> for the second person who approves it.</summary>
public sealed record SaveNonPayableItemRequest(Guid? PayerId, Guid? ServiceItemId, ServiceCategory? Category, string? Notes, string? Reason = null);

/// <summary>Why an item should be taken off the non-payable list, for the second person who approves it.</summary>
public sealed record RemoveNonPayableItemRequest(string? Reason);

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
    uint Version)
{
    /// <summary>
    /// Whether the signed-in user recorded the terms a verification vouches for (or last changed them), and so may not
    /// verify it: someone else does.
    /// </summary>
    public bool RecordedByYou { get; init; }
}

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
        RuleFor(x => x.Reason).SafeText(500);
    }
}

public sealed class SaveNonPayableItemRequestValidator : AbstractValidator<SaveNonPayableItemRequest>
{
    public SaveNonPayableItemRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.ServiceItemId is null) != (x.Category is null))
            .WithName("serviceItemId").WithMessage("Name either a service or a category.");
        RuleFor(x => x.Notes).SafeText(300);
        RuleFor(x => x.Reason).SafeText(500);
    }
}

public sealed class RemoveNonPayableItemRequestValidator : AbstractValidator<RemoveNonPayableItemRequest>
{
    public RemoveNonPayableItemRequestValidator() => RuleFor(x => x.Reason).SafeText(500);
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
