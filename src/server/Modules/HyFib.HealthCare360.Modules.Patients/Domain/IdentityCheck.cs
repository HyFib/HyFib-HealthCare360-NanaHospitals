namespace HyFib.HealthCare360.Modules.Patients.Domain;

public enum IdentityCheckMethod
{
    /// <summary>The Aadhaar holder's fingerprint, captured by a UIDAI registered device and checked by UIDAI.</summary>
    AadhaarFingerprint = 0,
}

public enum IdentityCheckOutcome
{
    /// <summary>UIDAI says the fingerprint belongs to the Aadhaar number.</summary>
    Verified = 0,

    /// <summary>UIDAI says it does not, or the number cannot be authenticated.</summary>
    NotVerified,

    /// <summary>UIDAI was asked but gave no answer about the person.</summary>
    Failed,
}

/// <summary>
/// One check of a patient's identity with UIDAI (SRS INT-BIO, optional patient identity verification): who checked,
/// when, on which device, and what UIDAI answered. Neither the Aadhaar number nor anything biometric is kept: only the
/// number's last four digits, as on the patient record, and UIDAI's references to the transaction.
/// </summary>
public sealed class PatientIdentityCheck : AuditableEntity
{
    public Guid PatientId { get; set; }

    /// <summary>The facility it was done at.</summary>
    public Guid? FacilityId { get; set; }

    public IdentityCheckMethod Method { get; set; }

    public IdentityCheckOutcome Outcome { get; set; }

    /// <summary>The outcome in words, as staff were shown it.</summary>
    public string Message { get; set; } = string.Empty;

    public string AadhaarLast4 { get; set; } = string.Empty;

    /// <summary>A Virtual ID was given rather than the Aadhaar number, so the number on the record could not be compared.</summary>
    public bool UsedVirtualId { get; set; }

    /// <summary>The check linked the Aadhaar number to a record that had none (its last four digits and search index).</summary>
    public bool LinkedAadhaar { get; set; }

    /// <summary><c>uidai</c>, or <c>simulator</c> for training.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>UIDAI's error code when it refused.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>The transaction id HC360 sent, and UIDAI's code for the authentication: what UIDAI traces it by.</summary>
    public string? TransactionId { get; set; }

    public string? ResponseCode { get; set; }

    /// <summary>The registered device's provider (<c>dpId</c>) and model (<c>mi</c>).</summary>
    public string? DeviceProviderId { get; set; }

    public string? DeviceModelId { get; set; }

    public string? CheckedByName { get; set; }
}
