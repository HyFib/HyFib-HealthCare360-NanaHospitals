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

    /// <summary>The patient was given the hospital's privacy notice (DPDP Act s.5, Rules 2025 rule 3), at the version in force.</summary>
    PrivacyNotice,
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

    /// <summary>Who recorded the withdrawal: a staff member, the patient on the portal, or null for an automatic one (a WhatsApp STOP).</summary>
    public Guid? RevokedByUserId { get; set; }

    public string? Notes { get; set; }

    public bool IsActive => Granted && RevokedAt is null;

    /// <summary>
    /// Standing permissions (sharing records, messages, video consultations, research, photographs) and the privacy notice
    /// given hold until the patient withdraws them, so a withdrawal or a later refusal ends every earlier grant of the type. The other types
    /// are consents for one stay or procedure (general treatment, a high-risk procedure, anaesthesia, a transfusion): a
    /// patient may have several, and each is withdrawn on its own.
    /// </summary>
    public static bool IsStandingPermission(ConsentType type) => type is ConsentType.DataSharing or ConsentType.SmsWhatsAppUpdates
        or ConsentType.MarketingCommunication or ConsentType.Telehealth or ConsentType.Research or ConsentType.Photography
        or ConsentType.PrivacyNotice;

    /// <summary>
    /// Withdraws a consent that was given. The record stays as history, marked with when, by whom and why. A refusal
    /// has nothing to withdraw (a new consent is recorded if the patient now agrees), and a withdrawal is final.
    /// </summary>
    public Result Revoke(string reason, Guid? userId, DateTimeOffset at)
    {
        if (RevokedAt is not null)
        {
            return Error.Rule("consent.revoked", "This consent has already been withdrawn.");
        }

        if (!Granted)
        {
            return Error.Rule("consent.not_granted", "This records a refusal, so there is nothing to withdraw. Record a new consent if the patient now agrees.");
        }

        RevokedAt = at;
        RevokedReason = reason.Trim();
        RevokedByUserId = userId;
        return Result.Success();
    }
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
