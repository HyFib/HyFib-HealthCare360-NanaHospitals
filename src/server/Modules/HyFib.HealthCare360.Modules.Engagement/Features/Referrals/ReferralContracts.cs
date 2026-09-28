namespace HyFib.HealthCare360.Modules.Engagement.Features.Referrals;

public sealed record ReferringDoctorListItem(
    Guid Id,
    string Name,
    string? Clinic,
    string? Specialty,
    string? City,
    string? Phone,
    bool IsActive,
    int Referrals,
    DateTimeOffset? LastReferralAt);

public sealed record ReferringDoctorDetail(
    Guid Id,
    string Name,
    string? Clinic,
    string? Phone,
    string? Email,
    string? Specialty,
    string? City,
    string? RegistrationNumber,
    IReadOnlyList<string> AlternateNames,
    string? Notes,
    bool IsActive,
    uint Version);

public sealed record SaveReferringDoctorRequest(
    string Name,
    string? Clinic,
    string? Phone,
    string? Email,
    string? Specialty,
    string? City,
    string? RegistrationNumber,
    IReadOnlyList<string>? AlternateNames,
    string? Notes,
    bool IsActive,
    uint? Version);

/// <summary>Referral volume for one doctor in a period (counts only — no patient details).</summary>
public sealed record ReferralStats(
    Guid DoctorId,
    DateOnly From,
    DateOnly To,
    int Referrals,
    DateTimeOffset? LastReferralAt,
    IReadOnlyList<CountByLabel> ByMonth,
    IReadOnlyList<CountByLabel> ByDepartment,
    IReadOnlyList<CountByLabel> ByVisitType);

public sealed class SaveReferringDoctorRequestValidator : AbstractValidator<SaveReferringDoctorRequest>
{
    public const int MaxAlternateNames = 10;

    public SaveReferringDoctorRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Clinic).SafeText(200);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Specialty).SafeText(100);
        RuleFor(x => x.City).SafeText(100);
        RuleFor(x => x.RegistrationNumber).SafeText(64);
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.AlternateNames).Must(n => n is null || n.Count <= MaxAlternateNames)
            .WithMessage($"Add at most {MaxAlternateNames} other spellings.");
        RuleForEach(x => x.AlternateNames).NotEmpty().MaximumLength(200);
    }
}
