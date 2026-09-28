using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

public sealed record PatientListItem(
    Guid Id,
    string Uhid,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Age,
    string? PhoneMasked,
    PatientCategory Category,
    bool IsVip,
    bool IsDeceased,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastVisitAt);

public sealed record PatientDetail(
    Guid Id,
    string Uhid,
    string? Title,
    string FirstName,
    string? MiddleName,
    string? LastName,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    bool IsDateOfBirthEstimated,
    string? Age,
    BloodGroup BloodGroup,
    MaritalStatus MaritalStatus,
    string? Phone,
    string? AlternatePhone,
    string? Email,
    Address Address,
    string? GuardianName,
    GuardianRelation? GuardianRelation,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelation,
    string? Occupation,
    string? Religion,
    string? Nationality,
    string? PreferredLanguage,
    PatientCategory Category,
    string? AbhaNumberMasked,
    string? AbhaAddress,
    string? AadhaarLast4,
    Guid? PhotoFileId,
    Guid RegisteredFacilityId,
    DateTimeOffset RegisteredAt,
    bool IsVip,
    bool IsDeceased,
    DateTimeOffset? DeceasedAt,
    Guid? MergedIntoPatientId,
    string? Notes,
    bool IsActive,
    uint Version);

/// <summary>Compact identity used by banners, pickers and other modules.</summary>
public sealed record PatientSummary(
    Guid Id,
    string Uhid,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Age,
    string? Phone,
    BloodGroup BloodGroup,
    PatientCategory Category,
    bool IsVip,
    bool IsDeceased,
    Guid? PhotoFileId);

public sealed record RegisterPatientRequest(
    string? Title,
    string FirstName,
    string? MiddleName,
    string? LastName,
    Gender Gender,
    DateOnly? DateOfBirth,
    int? AgeYears,
    int? AgeMonths,
    string? Phone,
    string? AlternatePhone,
    string? Email,
    Address? Address,
    BloodGroup? BloodGroup,
    MaritalStatus? MaritalStatus,
    string? GuardianName,
    GuardianRelation? GuardianRelation,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelation,
    string? Occupation,
    string? Religion,
    string? Nationality,
    string? PreferredLanguage,
    PatientCategory? Category,
    string? AbhaNumber,
    string? AbhaAddress,
    string? AadhaarNumber,
    Guid? PhotoFileId,
    bool IsVip,
    string? Notes,
    bool ConfirmedNotDuplicate);

public sealed record UpdatePatientRequest(
    string? Title,
    string FirstName,
    string? MiddleName,
    string? LastName,
    Gender Gender,
    DateOnly? DateOfBirth,
    int? AgeYears,
    int? AgeMonths,
    string? Phone,
    string? AlternatePhone,
    string? Email,
    Address? Address,
    BloodGroup? BloodGroup,
    MaritalStatus? MaritalStatus,
    string? GuardianName,
    GuardianRelation? GuardianRelation,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelation,
    string? Occupation,
    string? Religion,
    string? Nationality,
    string? PreferredLanguage,
    PatientCategory? Category,
    string? AbhaNumber,
    string? AbhaAddress,
    string? AadhaarNumber,
    Guid? PhotoFileId,
    bool IsVip,
    bool IsDeceased,
    DateTimeOffset? DeceasedAt,
    string? Notes,
    bool IsActive,
    uint Version);

public sealed record PatientCreated(Guid Id, string Uhid);

public sealed record DuplicateCheckRequest(string? FirstName, string? LastName, DateOnly? DateOfBirth, string? Phone, string? AbhaNumber, string? AadhaarNumber);

public sealed record DuplicateCandidate(Guid Id, string Uhid, string FullName, Gender Gender, string? Age, string? PhoneMasked, int Score, string Reason);

public sealed record MergePatientRequest(Guid IntoPatientId, string Reason);

public sealed record BreakGlassRequest(string Reason);

public sealed record BreakGlassGrant(Guid PatientId, DateTimeOffset ExpiresAt);

internal static class PatientRules
{
    /// <summary>
    /// Letters in any script plus combining marks (Devanagari/Tamil vowel signs are marks, not letters), spaces,
    /// dots, apostrophes and hyphens.
    /// </summary>
    public static bool ValidName(string? value) =>
        value is null || value.All(c => char.IsLetter(c) || IsCombiningMark(c) || c is ' ' or '.' or '\'' or '-');

    private static bool IsCombiningMark(char c) =>
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark;

    /// <summary>Aadhaar: 12 digits, first digit 2–9, valid Verhoeff checksum.</summary>
    public static bool ValidAadhaar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 12 && digits[0] is >= '2' and <= '9' && Verhoeff.IsValid(digits);
    }

    public static DateOnly? ResolveDateOfBirth(DateOnly? dateOfBirth, int? ageYears, int? ageMonths, DateOnly today, out bool estimated)
    {
        estimated = false;
        if (dateOfBirth is not null)
        {
            return dateOfBirth;
        }

        if (ageYears is null && ageMonths is null)
        {
            return null;
        }

        estimated = true;
        return Age.EstimateDateOfBirth(ageYears ?? 0, ageMonths ?? 0, today);
    }
}

/// <summary>Verhoeff checksum used by Aadhaar numbers.</summary>
internal static class Verhoeff
{
    private static readonly int[,] D =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 },
        { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 }, { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 },
        { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 }, { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 },
        { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly int[,] P =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 },
        { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 }, { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 },
        { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    public static bool IsValid(string digits)
    {
        var check = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i] - '0';
            check = D[check, P[i % 8, digit]];
        }

        return check == 0;
    }
}

public sealed class RegisterPatientRequestValidator : AbstractValidator<RegisterPatientRequest>
{
    public RegisterPatientRequestValidator(TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        RuleFor(x => x.FirstName).NotEmpty().WithMessage("First name is required.").MaximumLength(100)
            .Must(PatientRules.ValidName).WithMessage("Use letters, spaces, dots, apostrophes or hyphens.");
        RuleFor(x => x.MiddleName).MaximumLength(100).Must(PatientRules.ValidName).WithMessage("Use letters only.");
        RuleFor(x => x.LastName).MaximumLength(100).Must(PatientRules.ValidName).WithMessage("Use letters only.");
        RuleFor(x => x.Title).MaximumLength(16);
        RuleFor(x => x.DateOfBirth)
            .Must(d => d is null || (d <= today && d.Value.Year >= 1900)).WithMessage("Enter a valid date of birth.");
        RuleFor(x => x)
            .Must(x => x.DateOfBirth is not null || x.AgeYears is not null || x.AgeMonths is not null)
            .WithName("dateOfBirth").WithMessage("Enter the date of birth or the age.");
        RuleFor(x => x.AgeYears).InclusiveBetween(0, 130);
        RuleFor(x => x.AgeMonths).InclusiveBetween(0, 11);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.AlternatePhone).PhoneNumber();
        RuleFor(x => x.EmergencyContactPhone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address!.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.Address?.PinCode));
        RuleFor(x => x.AbhaNumber).AbhaNumber();
        RuleFor(x => x.AadhaarNumber).Must(PatientRules.ValidAadhaar).WithMessage("Enter a valid 12-digit Aadhaar number.");
        RuleFor(x => x.AbhaAddress).MaximumLength(128);
        RuleFor(x => x.GuardianName).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class UpdatePatientRequestValidator : AbstractValidator<UpdatePatientRequest>
{
    public UpdatePatientRequestValidator(TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100).Must(PatientRules.ValidName).WithMessage("Use letters, spaces, dots, apostrophes or hyphens.");
        RuleFor(x => x.MiddleName).MaximumLength(100).Must(PatientRules.ValidName);
        RuleFor(x => x.LastName).MaximumLength(100).Must(PatientRules.ValidName);
        RuleFor(x => x.DateOfBirth).Must(d => d is null || (d <= today && d.Value.Year >= 1900)).WithMessage("Enter a valid date of birth.");
        RuleFor(x => x.AgeYears).InclusiveBetween(0, 130);
        RuleFor(x => x.AgeMonths).InclusiveBetween(0, 11);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.AlternatePhone).PhoneNumber();
        RuleFor(x => x.EmergencyContactPhone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address!.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.Address?.PinCode));
        RuleFor(x => x.AbhaNumber).AbhaNumber();
        RuleFor(x => x.AadhaarNumber).Must(PatientRules.ValidAadhaar).WithMessage("Enter a valid 12-digit Aadhaar number.");
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class MergePatientRequestValidator : AbstractValidator<MergePatientRequest>
{
    public MergePatientRequestValidator()
    {
        RuleFor(x => x.IntoPatientId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).MaximumLength(500).WithMessage("Explain why these records are the same person (at least 10 characters).");
    }
}

public sealed class BreakGlassRequestValidator : AbstractValidator<BreakGlassRequest>
{
    public BreakGlassRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(15).MaximumLength(500).WithMessage("Describe the emergency (at least 15 characters).");
    }
}
