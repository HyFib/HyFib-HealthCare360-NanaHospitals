namespace HyFib.HealthCare360.Modules.Privacy.Domain;

/// <summary>
/// An erasure from the erasure ledger applied again because the database did not have it: it was restored from a backup
/// made before the erasure. One row per ledger entry (<see cref="LedgerEntryId"/>), so an entry is applied once. Only ids
/// and item keys: no personal data.
/// </summary>
public sealed class ErasureReplay : Entity
{
    /// <summary>The ledger entry applied (<see cref="Services.ErasureLedgerEntry.Id"/>).</summary>
    public Guid LedgerEntryId { get; set; }

    public Guid RequestId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }

    /// <summary>When the erasure was first made.</summary>
    public DateTimeOffset ErasedAt { get; set; }

    public DateTimeOffset ReplayedAt { get; set; }

    /// <summary>The items applied again.</summary>
    public List<string> Applied { get; set; } = [];

    /// <summary>The items held back (a portal account other patients now sign in with), for the grievance officer to see to.</summary>
    public List<string> HeldBack { get; set; } = [];

    /// <summary>The patient is not in the restored database (registered after the backup was made), so there was nothing to erase.</summary>
    public bool PatientMissing { get; set; }
}
