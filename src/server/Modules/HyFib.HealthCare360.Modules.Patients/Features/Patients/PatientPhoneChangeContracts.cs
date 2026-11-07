using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>Sends a code to <paramref name="Phone"/>, the patient's new mobile number, to confirm the change with.</summary>
public sealed record RequestPhoneChangeRequest(string Phone);

/// <summary>The code the patient read out from the SMS on the new number.</summary>
public sealed record ConfirmPhoneChangeRequest(string Code);

/// <summary>
/// A change waiting for its code: the new number masked, until when the code works, and how many tries are left. The
/// answer to sending a code (202), and to asking for the patient's pending change.
/// </summary>
public sealed record PhoneChangeDto(Guid ChangeId, string MaskedPhone, DateTimeOffset ExpiresAt, int AttemptsLeft);

/// <summary>
/// The number changed: the new number masked, how many family members' records the old number's portal account saw
/// that the new number's now sees (<paramref name="FamilyLinksMoved"/>), and whether the old number's portal account was
/// signed out because it no longer reaches any patient (<paramref name="OldNumberSignedOut"/>).
/// </summary>
public sealed record PhoneChangeConfirmed(string MaskedPhone, int FamilyLinksMoved, bool OldNumberSignedOut);

public sealed class RequestPhoneChangeRequestValidator : AbstractValidator<RequestPhoneChangeRequest>
{
    public RequestPhoneChangeRequestValidator() =>
        RuleFor(x => x.Phone).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(PatientPhoneChange.IndianMobileMessage)
            .MaximumLength(20).WithMessage(PatientPhoneChange.IndianMobileMessage)
            .Must(phone => Phone.IsIndianMobile(Phone.Normalize(phone))).WithMessage(PatientPhoneChange.IndianMobileMessage);
}

public sealed class ConfirmPhoneChangeRequestValidator : AbstractValidator<ConfirmPhoneChangeRequest>
{
    public ConfirmPhoneChangeRequestValidator() => RuleFor(x => x.Code).SixDigitCode("Enter the 6-digit code sent to the new number.");
}
