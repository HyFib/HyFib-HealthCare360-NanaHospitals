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
