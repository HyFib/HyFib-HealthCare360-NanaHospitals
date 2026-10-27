using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

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
    uint Version,
    bool ContactsMasked = false);

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

/// <param name="PrivacyNoticeGiven">
/// The desk gave the patient the privacy notice (DPDP s.5): recorded as a <see cref="ConsentType.PrivacyNotice"/> consent
/// with the notice's current version (<see cref="PrivacyNotice"/>).
/// </param>
/// <param name="Paper">
/// The patient was registered on paper while HC360 was down, under a downtime UHID of the desk's kit: the record keeps
/// that UHID and the time on the form (<see cref="Services.DowntimeUhidService"/>). Null for a registration made now.
/// </param>
/// <param name="WhatsAppOptIn">
/// The patient agreed at the desk to get reminders and updates on WhatsApp on <paramref name="Phone"/> (INT-WA); recorded
/// as a registration-desk opt-in with the registering user.
/// </param>
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
    bool ConfirmedNotDuplicate,
    bool WhatsAppOptIn = false,
    bool PrivacyNoticeGiven = false,
    PaperRegistration? Paper = null);

/// <summary>A registration entered from the paper form filled in while HC360 was down.</summary>
/// <param name="Uhid">The downtime UHID written on the form.</param>
/// <param name="RegisteredAt">When the form says the patient was registered.</param>
public sealed record PaperRegistration(string Uhid, DateTimeOffset RegisteredAt);

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
    uint Version,
    bool RemovePhoto = false);

public sealed record PatientCreated(Guid Id, string Uhid);

public sealed record DuplicateCheckRequest(string? FirstName, string? LastName, DateOnly? DateOfBirth, string? Phone, string? AbhaNumber, string? AadhaarNumber);

/// <summary>
/// The registration form asks while it is being filled in, so a detail half typed is not an error here: each is held to
/// the length the patient's record keeps, and a search with none of them finds nobody.
/// </summary>
public sealed class DuplicateCheckRequestValidator : AbstractValidator<DuplicateCheckRequest>
{
    public DuplicateCheckRequestValidator()
    {
        RuleFor(x => x.FirstName).SafeText(100);
        RuleFor(x => x.LastName).SafeText(100);
        RuleFor(x => x.Phone).SafeText(20);
        RuleFor(x => x.AbhaNumber).SafeText(20);
        RuleFor(x => x.AadhaarNumber).SafeText(20);
    }
}

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

        return AadhaarNumber.IsAadhaar(new string(value.Where(char.IsAsciiDigit).ToArray()));
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

public sealed class RegisterPatientRequestValidator : AbstractValidator<RegisterPatientRequest>
{
    public RegisterPatientRequestValidator(TimeProvider time)
    {
        // The hospital's date (IST), a day ahead of UTC's between midnight and 05:30: a baby born then is born today.
        var today = time.Today();

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
        RuleFor(x => x.WhatsAppOptIn).Equal(false).When(x => string.IsNullOrWhiteSpace(x.Phone))
            .WithMessage("Enter the mobile number the patient wants WhatsApp messages on.");
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address!.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.Address?.PinCode));
        RuleFor(x => x.AbhaNumber).AbhaNumber();
        RuleFor(x => x.AadhaarNumber).Must(PatientRules.ValidAadhaar).WithMessage("Enter a valid 12-digit Aadhaar number.");
        RuleFor(x => x.AbhaAddress).MaximumLength(128).AbhaAddress();
        RuleFor(x => x.GuardianName).MaximumLength(200);
        RuleFor(x => x.PhotoFileId).NotEqual(Guid.Empty).WithMessage("Choose a valid photo.");
        RuleFor(x => x.Notes).SafeText(2000);
        RuleFor(x => x.Paper!.Uhid).NotEmpty().WithMessage("Enter the UHID written on the paper form.").MaximumLength(20)
            .OverridePropertyName("paper.uhid").When(x => x.Paper is not null);
        RuleFor(x => x.Paper!.RegisteredAt)
            .Must(at => at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The registration time cannot be in the future.")
            .Must(at => at >= time.GetUtcNow() - Services.DowntimeUhidService.LongestBackEntry)
            .WithMessage("Paper registrations older than 90 days cannot be entered this way.")
            .OverridePropertyName("paper.registeredAt").When(x => x.Paper is not null);
    }
}

public sealed class UpdatePatientRequestValidator : AbstractValidator<UpdatePatientRequest>
{
    public UpdatePatientRequestValidator(TimeProvider time)
    {
        var today = time.Today();

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

        // The form sends the stored ABHA address back with every edit, so its format is checked by the service, and only
        // when it changes: an address recorded before the check existed must not block correcting a phone number.
        RuleFor(x => x.AbhaAddress).MaximumLength(128);
        RuleFor(x => x.AadhaarNumber).Must(PatientRules.ValidAadhaar).WithMessage("Enter a valid 12-digit Aadhaar number.");
        RuleFor(x => x.PhotoFileId).NotEqual(Guid.Empty).WithMessage("Choose a valid photo.");

        // No photo in the request keeps the current one; a photo is replaced by sending the new one, or removed with RemovePhoto.
        RuleFor(x => x).Must(x => !(x.RemovePhoto && x.PhotoFileId is not null))
            .WithName("photoFileId").WithMessage("Choose a new photo or remove the current one, not both.");
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

/// <summary>The sensitive categories a patient's record is in (R-4), with the patient's version for the next change.</summary>
public sealed record SensitiveCategoriesDto(IReadOnlyList<SensitiveCategory> Categories, uint Version);

/// <param name="Categories">Every category the record is in after the change; empty takes it out of all of them.</param>
/// <param name="Reason">Why: the diagnosis, or the patient's request; kept in the audit trail.</param>
public sealed record SetSensitiveCategoriesRequest(IReadOnlyList<SensitiveCategory> Categories, string Reason, uint Version);

public sealed class SetSensitiveCategoriesRequestValidator : AbstractValidator<SetSensitiveCategoriesRequest>
{
    public SetSensitiveCategoriesRequestValidator()
    {
        RuleFor(x => x.Categories).NotNull();
        RuleForEach(x => x.Categories).IsInEnum().NotEqual(SensitiveCategory.None);
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).MaximumLength(500).WithMessage("Give the reason (at least 10 characters).");
    }
}
