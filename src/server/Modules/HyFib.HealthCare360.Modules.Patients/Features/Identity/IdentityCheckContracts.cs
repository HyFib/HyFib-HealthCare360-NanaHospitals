using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Features.Identity;

/// <summary>A patient's fingerprint to check against their Aadhaar number with UIDAI.</summary>
/// <param name="AadhaarNumber">The 12-digit Aadhaar number or the 16-digit Virtual ID, as the patient gives it; never kept.</param>
/// <param name="PidData">The fingerprint reader's capture, as HC360 Desktop returned it (encrypted for UIDAI).</param>
/// <param name="Consent">The patient agreed to Aadhaar authentication after being told what it is for.</param>
public sealed record VerifyIdentityRequest(string AadhaarNumber, string PidData, bool Consent);

public sealed record IdentityCheckDto(
    Guid Id,
    IdentityCheckMethod Method,
    IdentityCheckOutcome Outcome,
    string Message,
    string AadhaarLast4,
    bool UsedVirtualId,
    bool LinkedAadhaar,
    string Provider,
    string? ErrorCode,
    string? TransactionId,
    string? ResponseCode,
    string? DeviceProviderId,
    string? DeviceModelId,
    DateTimeOffset CheckedAt,
    string? CheckedByName);

public sealed class VerifyIdentityRequestValidator : AbstractValidator<VerifyIdentityRequest>
{
    /// <summary>A capture of two fingers as minutiae and images stays well under this (the RD service limit).</summary>
    public const int MaxCaptureChars = 2 * 1024 * 1024;

    public VerifyIdentityRequestValidator()
    {
        RuleFor(x => x.AadhaarNumber).Must(value => AadhaarNumber.Normalize(value) is not null)
            .WithMessage("Enter the 12-digit Aadhaar number or the 16-digit Virtual ID.");
        RuleFor(x => x.PidData).NotEmpty().WithMessage("Capture the fingerprint first.").MaximumLength(MaxCaptureChars);
        RuleFor(x => x.Consent).Equal(true).WithMessage("Ask for the patient's consent to Aadhaar authentication first.");
    }
}
