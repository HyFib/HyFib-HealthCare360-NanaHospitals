using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Organization;

/// <summary>A hospital or branch. Facility-scoped data is filtered to the user's current facility.</summary>
public sealed class Facility : AuditableEntity
{
    /// <summary>2–6 upper-case letters, used as the prefix in UHID and document numbers.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public Address Address { get; set; } = new();
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? Gstin { get; set; }
    public string? Pan { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? NabhNumber { get; set; }

    /// <summary>ABDM Health Facility Registry id.</summary>
    public string? HfrId { get; set; }

    public string TimeZone { get; set; } = "Asia/Kolkata";
    public string Currency { get; set; } = "INR";
    public Guid? LogoFileId { get; set; }

    /// <summary>Extra letterhead line (for example accreditation text) printed on documents.</summary>
    public string? LetterheadNote { get; set; }

    public bool IsActive { get; set; } = true;
}

public enum DepartmentKind
{
    Clinical = 0,
    Diagnostic,
    Support,
    Administrative,
}

public sealed class Department : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DepartmentKind Kind { get; set; }
    public Guid? SpecialtyId { get; set; }
    public Guid? HeadPractitionerId { get; set; }
    public string? Location { get; set; }
    public string? Phone { get; set; }

    /// <summary>Queue token prefix shown on display boards (for example "GM" → GM-012).</summary>
    public string? TokenPrefix { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Specialty : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum PractitionerKind
{
    Consultant = 0,
    Resident,
    Visiting,
    Nurse,
    Therapist,
    Technician,
    Dietitian,
    Other,
}

/// <summary>
/// The practitioners who are doctors: they need their medical council registration to consult and sign, and a
/// consultation with one is charged at their fee (the rate list lists each of them).
/// </summary>
public static class PractitionerKinds
{
    public static IReadOnlyList<PractitionerKind> Doctors { get; } = [PractitionerKind.Consultant, PractitionerKind.Resident, PractitionerKind.Visiting];

    public static bool IsDoctor(PractitionerKind kind) => Doctors.Contains(kind);
}

/// <summary>A doctor or other clinician. Linked to a user account for sign-in and to HR for employment.</summary>
public sealed class Practitioner : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = "Dr.";
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public Gender Gender { get; set; }
    public string? Qualification { get; set; }
    public string? RegistrationCouncil { get; set; }
    public string? RegistrationNumber { get; set; }

    /// <summary>
    /// The professional's id in ABDM's Healthcare Professionals Registry, written <c>xx-xxxx-xxxx-xxxx</c>
    /// (<see cref="Hpr.Format"/>): it identifies them in ABDM health records and NHCX claims.
    /// </summary>
    public string? HprId { get; set; }

    public Guid? SpecialtyId { get; set; }
    public Guid? DepartmentId { get; set; }
    public PractitionerKind Kind { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public decimal ConsultationFeeNew { get; set; }
    public decimal ConsultationFeeFollowUp { get; set; }

    /// <summary>Days after a paid consultation during which follow-ups are free/discounted.</summary>
    public int FollowUpValidityDays { get; set; } = 7;

    public List<string> Languages { get; set; } = [];
    public string? Bio { get; set; }
    public Guid? PhotoFileId { get; set; }
    public Guid? SignatureFileId { get; set; }
    public bool AvailableForOnlineBooking { get; set; }
    public bool AvailableForTelehealth { get; set; }

    /// <summary>Facilities where this practitioner consults (empty = all).</summary>
    public List<Guid> FacilityIds { get; set; } = [];

    public bool IsActive { get; set; } = true;

    public static string ComposeDisplayName(string title, string firstName, string? lastName) =>
        string.Join(' ', new[] { title, firstName, lastName }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
}

public enum ServiceCategory
{
    Registration = 0,
    Consultation,
    Laboratory,
    Radiology,
    Cardiology,
    Procedure,
    Surgery,
    BedCharge,
    Nursing,
    DoctorVisit,
    Package,
    HealthCheckup,
    Physiotherapy,
    Dialysis,
    Dietary,
    BloodBank,
    Ambulance,
    Consumable,
    Miscellaneous,
}

/// <summary>
/// A billable service in the enterprise catalogue (consultation, test, procedure, bed day…). Prices per
/// tariff plan (general, insurance, corporate) are defined by the Billing module.
/// </summary>
public sealed class ServiceItem : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public ServiceCategory Category { get; set; }
    public Guid? DepartmentId { get; set; }
    public decimal BasePrice { get; set; }

    /// <summary>GST percentage (most healthcare services are exempt, i.e. 0).</summary>
    public decimal TaxRate { get; set; }

    /// <summary>Services Accounting Code for GST invoices.</summary>
    public string? SacCode { get; set; }

    public bool IsDiscountAllowed { get; set; } = true;
    public bool IsPriceEditable { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The name in local languages, by language code (<see cref="Localization.LocalLanguages"/>: "ta", "hi", "kn"). The
    /// rate list of a facility prints the one in its local language under the English name (Clinical Establishments
    /// Rules 2012, r.9(i): rates displayed in the local language as well as in English).
    /// </summary>
    public Dictionary<string, string> LocalNames { get; set; } = [];
}
