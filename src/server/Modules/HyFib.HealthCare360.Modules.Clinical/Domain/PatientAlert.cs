namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum PatientAlertType
{
    /// <summary>Do not resuscitate order.</summary>
    Dnr = 0,
    FallRisk,

    /// <summary>Isolation precautions (contact, droplet, airborne).</summary>
    Isolation,

    /// <summary>Infection-control organism flag (MRSA, VRE, CRE, TB…).</summary>
    InfectionControl,
    Pregnancy,
    Custom,
}

public enum AlertSeverity
{
    Info = 0,
    Warning,
    Critical,
}

/// <summary>
/// A critical or infection-control flag shown in the persistent patient banner on every clinical screen (EHR-03).
/// Alerts are deactivated, never deleted, so the history of (for example) a DNR decision stays on record.
/// </summary>
public sealed class PatientAlert : AuditableEntity
{
    public Guid PatientId { get; set; }
    public PatientAlertType Type { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Organism, precaution type, gestation, or free text.</summary>
    public string? Detail { get; set; }

    public AlertSeverity Severity { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }

    /// <summary>Optional end date (for example the expected delivery date of a pregnancy flag).</summary>
    public DateOnly? ExpiresOn { get; set; }

    public Guid? RaisedByUserId { get; set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset? DeactivatedAt { get; private set; }
    public Guid? DeactivatedByUserId { get; private set; }
    public string? DeactivationReason { get; private set; }

    public bool IsCurrent(DateOnly today) => IsActive && (ExpiresOn is null || ExpiresOn >= today);

    public Result Deactivate(string reason, Guid? userId, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Rule("alert.inactive", "This alert is already inactive.");
        }

        IsActive = false;
        DeactivatedAt = now;
        DeactivatedByUserId = userId;
        DeactivationReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>Default banner severity when the caller does not choose one.</summary>
    public static AlertSeverity DefaultSeverity(PatientAlertType type) => type switch
    {
        PatientAlertType.Dnr or PatientAlertType.Isolation or PatientAlertType.InfectionControl => AlertSeverity.Critical,
        PatientAlertType.FallRisk or PatientAlertType.Pregnancy => AlertSeverity.Warning,
        _ => AlertSeverity.Info,
    };
}
