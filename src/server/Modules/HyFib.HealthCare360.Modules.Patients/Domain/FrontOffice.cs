namespace HyFib.HealthCare360.Modules.Patients.Domain;

public enum EnquiryType
{
    General = 0,
    Appointment,
    DoctorAvailability,
    Billing,
    Insurance,
    HealthPackage,
    Admission,
    Complaint,
    Other,
}

public enum EnquiryChannel
{
    WalkIn = 0,
    Phone,
    WhatsApp,
    Email,
    Website,
}

public enum EnquiryStatus
{
    Open = 0,
    InProgress,
    Resolved,
    Converted,
}

/// <summary>Walk-in or phone enquiry at the front desk (can be converted to a patient or CRM lead).</summary>
public sealed class Enquiry : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public EnquiryType Type { get; set; }
    public EnquiryChannel Channel { get; set; }
    public string Details { get; set; } = string.Empty;
    public EnquiryStatus Status { get; set; }
    public string? Response { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public Guid? PatientId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public enum VisitorPassStatus
{
    Active = 0,
    CheckedOut,
    Expired,
    Revoked,
}

/// <summary>Pass for attendants/visitors of in-patients (ward access control).</summary>
public sealed class VisitorPass : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string PassNumber { get; set; } = string.Empty;
    public Guid? PatientId { get; set; }
    public string VisitorName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Relation { get; set; }
    public string? IdProofType { get; set; }
    public string? IdProofLast4 { get; set; }
    public string? Purpose { get; set; }
    public string? Location { get; set; }
    public bool IsAttendant { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public VisitorPassStatus Status { get; set; }
    public DateTimeOffset? CheckedOutAt { get; set; }
    public Guid? PhotoFileId { get; set; }
}

public enum ConsentType
{
    GeneralTreatment = 0,
    DataSharing,
    Telehealth,
    Research,
    SmsWhatsAppUpdates,
    MarketingCommunication,
    Photography,
    HighRiskProcedure,
    Anaesthesia,
    BloodTransfusion,
}

/// <summary>Recorded consent (EHR-06): version, who captured it, witness, proxy signer and revocation.</summary>
public sealed class PatientConsent : AuditableEntity
{
    public Guid PatientId { get; set; }
    public ConsentType Type { get; set; }
    public string ConsentVersion { get; set; } = "1.0";
    public bool Granted { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public Guid? CapturedByUserId { get; set; }
    public string? WitnessName { get; set; }

    /// <summary>Set when a guardian/relative consented on the patient's behalf.</summary>
    public string? SignedByRelation { get; set; }

    public string? SignedByName { get; set; }
    public Guid? DocumentFileId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? Notes { get; set; }

    public bool IsActive => Granted && RevokedAt is null;
}

public enum PatientDocumentType
{
    IdProof = 0,
    InsuranceCard,
    ExternalReport,
    ReferralLetter,
    Consent,
    Prescription,
    DischargeSummary,
    Imaging,
    Photo,
    Other,
}

/// <summary>Document attached to the patient record (the encrypted file itself is in the platform file store).</summary>
public sealed class PatientDocument : AuditableEntity
{
    public Guid PatientId { get; set; }
    public Guid FileId { get; set; }
    public PatientDocumentType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly? DocumentDate { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
}
