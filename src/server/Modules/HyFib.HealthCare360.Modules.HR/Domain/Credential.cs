namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum CredentialType
{
    MedicalCouncilRegistration = 0,
    NursingCouncil,
    BLS,
    ACLS,
    Other,
}

/// <summary>How close a credential is to expiry. Ordered so that a later stage is "more urgent".</summary>
public enum CredentialExpiryStage
{
    None = 0,
    DueIn30Days,
    DueIn7Days,
    Expired,
}

/// <summary>
/// Professional licence or certification (medical/nursing council registration, BLS, ACLS…). NABH requires that
/// clinicians practise only with valid credentials, so expiries are tracked and alerted at 30 and 7 days.
/// </summary>
public sealed class Credential : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }
    public CredentialType Type { get; set; }

    /// <summary>Display name, e.g. "Tamil Nadu Medical Council" or "PALS" for <see cref="CredentialType.Other"/>.</summary>
    public string Name { get; set; } = string.Empty;

    public string Number { get; set; } = string.Empty;
    public string? IssuingAuthority { get; set; }
    public DateOnly? IssuedOn { get; set; }

    /// <summary>Null for lifetime registrations.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Scanned certificate in the encrypted file store.</summary>
    public Guid? FileId { get; set; }

    public string? Notes { get; set; }

    /// <summary>Last reminder stage sent, so each stage alerts exactly once even across restarts.</summary>
    public CredentialExpiryStage LastAlertStage { get; set; }

    public DateOnly? LastAlertedOn { get; set; }

    /// <summary>Changes the validity period; a renewed credential starts its reminder cycle again.</summary>
    public void SetValidity(DateOnly? issuedOn, DateOnly? expiresOn)
    {
        if (ExpiresOn != expiresOn)
        {
            LastAlertStage = CredentialExpiryStage.None;
            LastAlertedOn = null;
        }

        IssuedOn = issuedOn;
        ExpiresOn = expiresOn;
    }

    public void MarkAlerted(CredentialExpiryStage stage, DateOnly today)
    {
        LastAlertStage = stage;
        LastAlertedOn = today;
    }
}

/// <summary>Pure expiry rules used by the daily reminder job and the credential screens.</summary>
public static class CredentialExpiryPolicy
{
    public const int FirstReminderDays = 30;
    public const int FinalReminderDays = 7;

    /// <summary>A credential expiring today is still valid today, so it is "due in 7 days", not expired.</summary>
    public static CredentialExpiryStage StageFor(DateOnly? expiresOn, DateOnly today)
    {
        if (expiresOn is not { } expiry)
        {
            return CredentialExpiryStage.None;
        }

        var daysLeft = expiry.DayNumber - today.DayNumber;
        return daysLeft switch
        {
            < 0 => CredentialExpiryStage.Expired,
            <= FinalReminderDays => CredentialExpiryStage.DueIn7Days,
            <= FirstReminderDays => CredentialExpiryStage.DueIn30Days,
            _ => CredentialExpiryStage.None,
        };
    }

    /// <summary>Alert only when the stage has advanced since the last reminder (30 days → 7 days → expired).</summary>
    public static bool ShouldAlert(CredentialExpiryStage current, CredentialExpiryStage lastAlerted) =>
        current != CredentialExpiryStage.None && current > lastAlerted;
}
