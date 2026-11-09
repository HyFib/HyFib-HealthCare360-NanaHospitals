using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Privacy.Domain;

/// <summary>
/// A consent manager registered with the Data Protection Board (DPDP Act 2023 s.2(g), s.6(7) to (9); DPDP Rules 2025,
/// rule 4 and the First Schedule): a platform through which a patient gives, manages, reviews and withdraws consent. The
/// hospital records the ones it accepts consent from. Each signs what it sends with a secret shared with the hospital
/// (HMAC-SHA256), kept encrypted.
/// </summary>
public sealed class ConsentManager : AuditableEntity
{
    /// <summary>Short, stable, in the address the consent manager calls: <c>/api/v1/consent-managers/{code}/events</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Its registration number with the Board.</summary>
    public string RegistrationNumber { get; set; } = string.Empty;

    /// <summary>Where withdrawals made at the hospital are sent; null for the built-in simulator.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The shared signing secret, encrypted with the data key.</summary>
    [SensitiveData]
    public string SecretCipher { get; set; } = string.Empty;

    /// <summary>The built-in simulator (training and demonstration only): withdrawals are recorded, not sent.</summary>
    public bool IsSimulator { get; set; }

    public bool IsActive { get; set; } = true;
}

public enum ConsentArtefactStatus
{
    Active = 0,
    Withdrawn,
    Expired,
}

/// <summary>Who withdrew a consent: the patient at the consent manager, or at the hospital (portal or desk).</summary>
public enum ConsentWithdrawalSource
{
    ConsentManager = 0,
    Portal,
    Staff,
}

/// <summary>
/// A consent artefact received from a consent manager: the patient's consent for one purpose, over named data items,
/// for a period (DPDP Rules 2025, First Schedule, Part B). While active it is recorded as the patient's consent of that
/// kind (<see cref="ConsentRecordId"/>), so every check that reads consents honours it: campaigns, update messages,
/// record sharing, research. Withdrawn at either end, or past <see cref="ValidUntil"/>, that consent is revoked; a
/// withdrawal made at the hospital is sent back to the consent manager.
/// </summary>
public sealed class ConsentArtefact : AuditableEntity
{
    public Guid ConsentManagerId { get; set; }

    /// <summary>The consent manager's id for the artefact; unique per consent manager.</summary>
    public string ArtefactId { get; set; } = string.Empty;

    public Guid PatientId { get; set; }
    public ConsentType Purpose { get; set; }

    /// <summary>What the consent covers, in the consent manager's words (for example <c>lab-reports</c>, <c>discharge-summary</c>).</summary>
    public List<string> DataItems { get; set; } = [];

    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public ConsentArtefactStatus Status { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>The consent recorded for it in the patient's consents (Patients module).</summary>
    public Guid? ConsentRecordId { get; set; }

    /// <summary>For a child: given by the parent or guardian whose verified consent is on record.</summary>
    public bool GivenByGuardian { get; set; }

    public DateTimeOffset? WithdrawnAt { get; set; }
    public ConsentWithdrawalSource? WithdrawnVia { get; set; }

    /// <summary>A withdrawal made at the hospital, once the consent manager acknowledged it; null while it is still to be sent.</summary>
    public DateTimeOffset? WithdrawalSentAt { get; set; }

    public int WithdrawalAttempts { get; set; }
    public string? WithdrawalError { get; set; }

    public bool IsInForce(DateTimeOffset now) => Status == ConsentArtefactStatus.Active && ValidFrom <= now && now < ValidUntil;

    /// <summary>The purposes a consent manager may give consent for: those outside health care, which need it.</summary>
    public static bool IsAcceptedPurpose(ConsentType purpose) =>
        purpose is ConsentType.DataSharing or ConsentType.Research or ConsentType.SmsWhatsAppUpdates or ConsentType.MarketingCommunication or ConsentType.Photography;
}

/// <summary>An event a consent manager sent, kept once by its id, so a repeated delivery changes nothing.</summary>
public sealed class ConsentManagerEvent : Entity
{
    public Guid ConsentManagerId { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ArtefactId { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public string Outcome { get; set; } = string.Empty;
}
