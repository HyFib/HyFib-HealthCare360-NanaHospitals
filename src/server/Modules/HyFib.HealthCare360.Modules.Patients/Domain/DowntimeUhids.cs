namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>
/// A block of UHIDs issued ahead of time for a downtime kit: while HC360 cannot be reached, the registration desk
/// writes each new patient's details on the paper registration form against the next UHID of the block, and the
/// patient leaves with a real UHID on their card and wristband. When HC360 is back, each form is entered as a
/// registration from paper (<see cref="DowntimeUhid"/>), keeping the UHID and the time on the form. The numbers come
/// from the UHID series like any other, so HC360 never issues them again.
/// </summary>
public sealed class DowntimeUhidBlock : AuditableEntity
{
    /// <summary>The facility whose desk keeps the kit.</summary>
    public Guid FacilityId { get; set; }

    /// <summary>What the kit is called where it is kept, for example "Main reception, kit 2".</summary>
    public string Label { get; set; } = string.Empty;

    public int Count { get; set; }
    public string FirstUhid { get; set; } = string.Empty;
    public string LastUhid { get; set; } = string.Empty;
    public string? IssuedByName { get; set; }
}

/// <summary>One UHID of a <see cref="DowntimeUhidBlock"/>: not yet used, entered for the patient registered on paper, or voided.</summary>
public sealed class DowntimeUhid : AuditableEntity
{
    public Guid BlockId { get; set; }

    /// <summary>Its place in the block, from 1, as printed on the kit's sheet.</summary>
    public int Position { get; set; }

    public string Uhid { get; set; } = string.Empty;

    /// <summary>The patient entered from the paper form under this UHID.</summary>
    public Guid? PatientId { get; set; }

    /// <summary>When the paper form says the patient was registered (the patient's registration time).</summary>
    public DateTimeOffset? PaperRegisteredAt { get; set; }

    /// <summary>When the form was entered in HC360, and by whom.</summary>
    public DateTimeOffset? EnteredAt { get; set; }

    public string? EnteredByName { get; set; }

    /// <summary>Set when the number will not be used: its label was spoilt or lost, or the kit was withdrawn.</summary>
    public DateTimeOffset? VoidedAt { get; set; }

    public string? VoidReason { get; set; }
    public string? VoidedByName { get; set; }

    public DowntimeUhidStatus Status => PatientId is not null
        ? DowntimeUhidStatus.Entered
        : VoidedAt is not null ? DowntimeUhidStatus.Voided : DowntimeUhidStatus.Open;
}

public enum DowntimeUhidStatus
{
    /// <summary>Not used yet, or used on paper and not entered yet: HC360 cannot tell which.</summary>
    Open = 0,
    Entered,
    Voided,
}
