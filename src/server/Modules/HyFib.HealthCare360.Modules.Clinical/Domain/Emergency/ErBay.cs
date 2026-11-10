namespace HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;

/// <summary>The part of the emergency department a bay is in: where the triage nurse sends a patient of each level.</summary>
public enum ErArea
{
    /// <summary>Resuscitation (red): ESI 1, and 2 that need a monitor and a crash cart at hand.</summary>
    Resuscitation = 0,

    /// <summary>Acute care (yellow): ESI 2 and 3.</summary>
    Acute,

    /// <summary>Minor injuries and ambulatory care (green): ESI 4 and 5.</summary>
    Minor,

    /// <summary>Observation: patients waiting for a result, a bed upstairs or a review.</summary>
    Observation,

    /// <summary>A room on its own: isolation, or a sexual-assault victim's examination in privacy.</summary>
    Isolation,
}

/// <summary>
/// A bed (bay, trolley or chair) of the emergency department. The department treats patients in bays, not in the ward's
/// beds: a bay holds one patient at a time, from when the nurse puts them in it to when they move or leave (the
/// disposition frees it). Bays are not charged; the ward's bed-day charges begin with admission.
/// </summary>
public sealed class ErBay : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"R1", "A4", "M2": what is painted on the bay, unique in the facility.</summary>
    public string Code { get; set; } = string.Empty;

    public ErArea Area { get; set; }

    /// <summary>The order on the board.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>A bay out of use (repair, cleaning after an infectious case) takes no patient; its patient, if any, stays until moved.</summary>
    public bool IsActive { get; set; } = true;
}
