namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum ItemType
{
    Drug = 0,
    Consumable,
    Surgical,
    Implant,
    General,
    Food,
    Linen,
    Spare,
}

/// <summary>Drugs &amp; Cosmetics Rules, 1945 schedule — decides how an item may be sold and which register it needs.</summary>
public enum DrugSchedule
{
    /// <summary>Not a drug, or not scheduled.</summary>
    None = 0,

    /// <summary>Over-the-counter medicine (no prescription needed).</summary>
    Otc,

    /// <summary>Schedule G: sold with a caution label; no prescription needed.</summary>
    G,

    /// <summary>Schedule H: prescription only.</summary>
    H,

    /// <summary>Schedule H1: prescription only and recorded in the H1 register (e.g. 3rd-generation antibiotics, psychotropics).</summary>
    H1,

    /// <summary>Schedule X: narcotic/psychotropic; prescription copy retained and a separate register kept.</summary>
    X,
}

/// <summary>Clinical criticality set by the drug &amp; therapeutics committee, used for VED analysis.</summary>
public enum VedClass
{
    /// <summary>Default: needed for effective care, short stock-outs are tolerable.</summary>
    Essential = 0,

    /// <summary>Life-saving; a stock-out is unacceptable.</summary>
    Vital,

    /// <summary>Useful but a stock-out has little clinical impact.</summary>
    Desirable,
}

/// <summary>Grouping for the item master (tablets, injections, sutures, linen…); used for filters and counts.</summary>
public sealed class ItemCategory : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ItemType DefaultType { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Item master shared by every facility of the hospital group (INV-01). Stock is always counted in the
/// <see cref="BaseUnit"/> (tablet, ml, vial, piece); purchases use the <see cref="PurchaseUnit"/> (strip, box,
/// bottle) and <see cref="PurchaseUnitFactor"/> converts between them, so a strip of 10 can be sold loose.
/// </summary>
public sealed class Item : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? BrandName { get; set; }
    public ItemType Type { get; set; }
    public Guid? CategoryId { get; set; }
    public string? Strength { get; set; }
    public string? DosageForm { get; set; }

    /// <summary>Printed pack description, e.g. "Strip of 10 tablets".</summary>
    public string? PackSize { get; set; }

    public string BaseUnit { get; set; } = "NOS";
    public string PurchaseUnit { get; set; } = "NOS";

    /// <summary>Base units in one purchase unit (10 tablets per strip).</summary>
    public decimal PurchaseUnitFactor { get; set; } = 1;

    public string? HsnCode { get; set; }

    /// <summary>GST percentage applied on purchase and sale.</summary>
    public decimal GstRate { get; set; }

    public DrugSchedule Schedule { get; set; }

    /// <summary>NDPS narcotic/psychotropic: register entry and balance tracking on every movement.</summary>
    public bool IsControlled { get; set; }

    /// <summary>Must be stored at 2–8 °C (insulin, vaccines); shown on picking lists and labels.</summary>
    public bool IsColdChain { get; set; }

    public VedClass VedClass { get; set; }

    /// <summary>Reorder when available stock (base units) falls to this level; 0 disables reorder alerts.</summary>
    public decimal ReorderLevel { get; set; }

    public decimal ReorderQuantity { get; set; }
    public decimal MinimumStock { get; set; }
    public decimal MaximumStock { get; set; }
    public string? Manufacturer { get; set; }
    public string? Barcode { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Prescription-only: Schedule H, H1, X or a controlled (NDPS) drug.</summary>
    public bool RequiresPrescription => NeedsPrescription(Schedule, IsControlled);

    /// <summary>Statutory register (H1/X/NDPS) with patient, prescriber and running balance.</summary>
    public bool RequiresRegister => NeedsRegister(Schedule, IsControlled);

    /// <summary>Items that must carry batch and expiry on receipt (medicines, consumables, implants).</summary>
    public bool TracksExpiry => Type is ItemType.Drug or ItemType.Consumable or ItemType.Surgical or ItemType.Implant or ItemType.Food;

    public static bool NeedsPrescription(DrugSchedule schedule, bool isControlled) =>
        isControlled || schedule is DrugSchedule.H or DrugSchedule.H1 or DrugSchedule.X;

    public static bool NeedsRegister(DrugSchedule schedule, bool isControlled) =>
        isControlled || schedule is DrugSchedule.H1 or DrugSchedule.X;

    public decimal ToBaseUnits(decimal purchaseQuantity) => purchaseQuantity * PurchaseUnitFactor;

    /// <summary>Master-data consistency rules that validators cannot see in isolation.</summary>
    public Result Validate()
    {
        if (PurchaseUnitFactor <= 0)
        {
            return Error.Field("purchaseUnitFactor", "Enter how many base units make one purchase unit.");
        }

        if (MaximumStock > 0 && MinimumStock > MaximumStock)
        {
            return Error.Field("minimumStock", "Minimum stock cannot be more than maximum stock.");
        }

        if ((IsControlled || Schedule != DrugSchedule.None) && Type != ItemType.Drug)
        {
            return Error.Field("schedule", "Only drugs can carry a schedule or the controlled-drug flag.");
        }

        return GstCalculator.IsStandardRate(GstRate)
            ? Result.Success()
            : Error.Field("gstRate", "Use a notified GST rate (0, 0.25, 3, 5, 12, 18, 28 or 40%).");
    }
}
