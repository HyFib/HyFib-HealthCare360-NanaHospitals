using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>
/// A verification of the patient's mobile number waiting for its code: the number masked, until when the code works, and
/// how many tries are left. The answer to sending a code (202), and to asking for the patient's pending verification.
/// </summary>
public sealed record PhoneVerificationDto(Guid VerificationId, string MaskedPhone, DateTimeOffset ExpiresAt, int AttemptsLeft);

/// <summary>The code the patient read out from the message on their mobile number.</summary>
public sealed record ConfirmPhoneVerificationRequest(string Code);

/// <summary>The number is verified: masked, and when.</summary>
public sealed record PhoneVerified(string MaskedPhone, DateTimeOffset VerifiedAt);

public sealed class ConfirmPhoneVerificationRequestValidator : AbstractValidator<ConfirmPhoneVerificationRequest>
{
    public ConfirmPhoneVerificationRequestValidator() =>
        RuleFor(x => x.Code).SixDigitCode("Enter the 6-digit code sent to the patient's mobile number.");
}
