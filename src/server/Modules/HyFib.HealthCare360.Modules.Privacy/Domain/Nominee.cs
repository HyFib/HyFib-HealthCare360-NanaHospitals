namespace HyFib.HealthCare360.Modules.Privacy.Domain;

/// <summary>
/// The person a patient names to exercise their data rights after their death or if they cannot act for themselves
/// (DPDP Act 2023 s14). One nominee per patient; naming a new one replaces the old, and removing it is a soft delete,
/// so the history stays in the audit trail.
/// </summary>
public sealed class Nominee : AuditableEntity
{
    public Guid PatientId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>In the patient's words, for example "Son" or "Sister".</summary>
    public string Relationship { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DataRequestChannel RecordedVia { get; set; }
}
