namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

/// <summary>The clinical purpose of a monitoring programme; decides the default thresholds a new enrolment starts with.</summary>
public enum RpmProgramKind
{
    Hypertension = 0,
    Diabetes,

    /// <summary>Heart failure, post-MI and arrhythmia follow-up.</summary>
    Cardiac,

    /// <summary>The first weeks at home after an admission.</summary>
    PostDischarge,

    Custom,
}

/// <summary>A remote patient monitoring programme offered by the hospital (RPM-01), for example "Hypertension control".</summary>
public sealed class RpmProgram : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RpmProgramKind Kind { get; set; }
    public string? Description { get; set; }

    /// <summary>Readings a patient is asked to send per day; shown with adherence.</summary>
    public int ExpectedReadingsPerDay { get; set; } = 1;

    public bool IsActive { get; set; } = true;
}
