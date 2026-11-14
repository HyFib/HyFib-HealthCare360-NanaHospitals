namespace HyFib.HealthCare360.Modules.Patients.Domain;

public enum PatientCategory
{
    General = 0,
    Staff,
    StaffDependent,
    SeniorCitizen,
    Vip,
    Corporate,
    Insurance,
    GovernmentScheme,
    BelowPovertyLine,
    Charity,
    International,
}

/// <summary>"S/O, D/O, W/O…" as printed on Indian registration cards.</summary>
public enum GuardianRelation
{
    SonOf = 0,
    DaughterOf,
    WifeOf,
    HusbandOf,
    FatherOf,
    MotherOf,
    Guardian,
}

/// <summary>
/// Master patient record, identified by a UHID shared across all facilities of the hospital group.
/// ABHA is stored encrypted with a blind index; for Aadhaar only the last four digits and a blind index
/// (for duplicate detection) are kept — the full number is never stored.
/// </summary>
public sealed class Patient : AuditableEntity
{
    public string Uhid { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }

    /// <summary>Denormalised "First Middle Last" used for fuzzy search (trigram index).</summary>
    public string FullName { get; set; } = string.Empty;

    public Gender Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public bool IsDateOfBirthEstimated { get; set; }
    public BloodGroup BloodGroup { get; set; }
    public MaritalStatus MaritalStatus { get; set; }

    /// <summary>Primary mobile in E.164 (+919876543210).</summary>
    public string? Phone { get; set; }

    /// <summary>
    /// When someone last showed that <see cref="Phone"/> reaches this patient (ASVS V6.6.1): the desk entered a code sent to
    /// it as the patient read it out, the number was changed with a code to the new number, or the portal account of the
    /// number gave the patient's date of birth or UHID. Null until then. See <see cref="IsPhoneVerified"/>.
    /// </summary>
    public DateTimeOffset? PhoneVerifiedAt { get; set; }

    /// <summary>
    /// The number (E.164) that was verified at <see cref="PhoneVerifiedAt"/>. The verification holds only while the record
    /// still has this number, so any change of the number, by whatever route, leaves the new one unverified until it is
    /// verified in turn.
    /// </summary>
    public string? PhoneVerifiedNumber { get; set; }

    public string? AlternatePhone { get; set; }
    public string? Email { get; set; }
    public Address Address { get; set; } = new();

    public string? GuardianName { get; set; }
    public GuardianRelation? GuardianRelation { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelation { get; set; }

    public string? Occupation { get; set; }
    public string? Religion { get; set; }
    public string? Nationality { get; set; } = "Indian";
    public string? PreferredLanguage { get; set; }
    public PatientCategory Category { get; set; }

    /// <summary>The 14-digit ABHA number (digits only), encrypted.</summary>
    [SensitiveData]
    public string? AbhaNumberCipher { get; set; }

    /// <summary>Blind index of the ABHA number, for search and duplicate checks; kept out of the audit trail like the number.</summary>
    [SensitiveData]
    public string? AbhaNumberIndex { get; set; }

    /// <summary>The number as staff see it: <c>XX-XXXX-XXXX-1234</c>.</summary>
    public string? AbhaNumberMasked { get; set; }

    /// <summary>ABHA address (<c>name@abdm</c>, or <c>@sbx</c> on the sandbox), lower case.</summary>
    public string? AbhaAddress { get; set; }
    public string? AadhaarLast4 { get; set; }

    [SensitiveData]
    public string? AadhaarIndex { get; set; }

    public Guid? PhotoFileId { get; set; }
    public Guid RegisteredFacilityId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>Restricted record: opening it requires a permission or an audited break-the-glass reason.</summary>
    public bool IsVip { get; set; }

    /// <summary>Confidential kinds of record in the chart (HIV, mental health, MTP): see <see cref="SensitiveCategory"/>.</summary>
    public SensitiveCategory SensitiveCategories { get; set; }

    public bool IsDeceased { get; set; }
    public DateTimeOffset? DeceasedAt { get; set; }
    public Guid? MergedIntoPatientId { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public void SetName(string? title, string firstName, string? middleName, string? lastName)
    {
        Title = Clean(title);
        FirstName = firstName.Trim();
        MiddleName = Clean(middleName);
        LastName = Clean(lastName);
        FullName = ComposeFullName(FirstName, MiddleName, LastName);
    }

    public string? AgeDisplay(DateOnly today) => DateOfBirth is { } dob ? Age.Format(dob, today) : null;

    /// <summary>
    /// The record's mobile number is known to reach the patient: it was verified, and it is still the number verified. Only
    /// then does the patient portal show the record to whoever signs in with the number, and do messages to it carry a
    /// report, a prescription, a bill or a discharge summary (VerifiedNumbers).
    /// </summary>
    public bool IsPhoneVerified => Phone is not null && PhoneVerifiedAt is not null && PhoneVerifiedNumber == Phone;

    /// <summary>Records that <see cref="Phone"/>, as it is now, was shown to reach the patient at <paramref name="now"/>. Nothing to verify without a number.</summary>
    public void MarkPhoneVerified(DateTimeOffset now)
    {
        if (Phone is null)
        {
            return;
        }

        PhoneVerifiedAt = now;
        PhoneVerifiedNumber = Phone;
    }

    /// <summary>Call once the UHID is allocated; notifies other modules in the same transaction.</summary>
    public void MarkRegistered() => Raise(new PatientRegistered(Id, Uhid, RegisteredFacilityId));

    public void MarkMergedInto(Guid targetPatientId)
    {
        MergedIntoPatientId = targetPatientId;
        IsActive = false;
        AbhaNumberIndex = null;
        AadhaarIndex = null;
        Raise(new PatientsMerged(Id, targetPatientId));
    }

    public static string ComposeFullName(string firstName, string? middleName, string? lastName) =>
        string.Join(' ', new[] { firstName, middleName, lastName }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PatientRegistered(Guid PatientId, string Uhid, Guid FacilityId) : DomainEvent;

public sealed record PatientsMerged(Guid SourcePatientId, Guid TargetPatientId) : DomainEvent;

/// <summary>
/// An audited, time-boxed emergency opening of a restricted (VIP or confidential) record by one user (EHR-07,
/// break-the-glass). Kept in the database rather than in one server's memory, so it holds on every API node behind the
/// reverse proxy and after a restart (SCAL-01). The reason, and the alert to those who review the audit trail, are in the
/// audit trail.
/// </summary>
[NotAudited]
public sealed class BreakGlassAccess : Entity
{
    public Guid PatientId { get; init; }
    public Guid UserId { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
