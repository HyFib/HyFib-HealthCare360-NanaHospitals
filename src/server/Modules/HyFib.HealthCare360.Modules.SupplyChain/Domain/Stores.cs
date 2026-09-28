namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum StoreType
{
    Central = 0,
    Pharmacy,
    Ward,
    OT,
    Lab,
    Canteen,
    Cssd,
}

/// <summary>
/// A physical stock location of one facility (central store, OP/IP pharmacy, ICU sub-store…). Every batch,
/// ledger row and stock document belongs to a store, so facility scoping of stores isolates stock per branch.
/// </summary>
public sealed class Store : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StoreType Type { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>User accountable for the store (signs indents, counts and adjustments).</summary>
    public Guid? InChargeUserId { get; set; }

    public string? Location { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Pharmacy stores sell and dispense to patients; other stores only issue internally.</summary>
    public bool IsDispensing => Type == StoreType.Pharmacy;
}
