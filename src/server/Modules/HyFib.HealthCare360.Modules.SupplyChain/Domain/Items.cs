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

    /// <summary>
    /// A high-alert medicine (ISMP): one that does serious harm when given in error, such as insulin, heparin and other
    /// anticoagulants, concentrated electrolytes (potassium chloride), opioids, chemotherapy, neuromuscular blockers and IV
    /// inotropes. Each hospital flags its own items. A dose given on the ward is witnessed by a second nurse, as a
    /// controlled drug's is.
    /// </summary>
    public bool IsHighAlert { get; set; }

    /// <summary>Must be stored at 2–8 °C (insulin, vaccines); shown on picking lists and labels.</summary>
    public bool IsColdChain { get; set; }

    public VedClass VedClass { get; set; }

    /// <summary>Reorder when available stock (base units) falls to this level; 0 disables reorder alerts.</summary>
    public decimal ReorderLevel { get; set; }

    public decimal ReorderQuantity { get; set; }
    public decimal MinimumStock { get; set; }
    public decimal MaximumStock { get; set; }

    /// <summary>The manufacturer, or for an imported device the importer: a device's bill names it (NPPA).</summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// For an implant or another device, what it is as its bill must say: for a stent, its category (bare-metal or
    /// drug-eluting) first, then what it is made of and its size ("Drug-eluting, everolimus, cobalt-chromium, 3.0 × 28 mm").
    /// </summary>
    public string? Specification { get; set; }

    /// <summary>
    /// For a price-controlled implant (a coronary stent, a knee implant), the NPPA ceiling price of a base unit with the GST
    /// on it: no patient is charged more, whatever the MRP printed on an older pack. NPPA revises it, so the store updates it.
    /// </summary>
    public decimal? CeilingPrice { get; set; }

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

        if ((IsControlled || IsHighAlert || Schedule != DrugSchedule.None) && Type != ItemType.Drug)
        {
            return Error.Field("schedule", "Only drugs can carry a schedule, the controlled-drug flag or the high-alert flag.");
        }

        if (CeilingPrice is not null && (Type != ItemType.Implant || CeilingPrice <= 0))
        {
            return Error.Field("ceilingPrice", "An NPPA ceiling price is set on an implant (a coronary stent, a knee implant), and is more than zero.");
        }

        return GstCalculator.IsStandardRate(GstRate)
            ? Result.Success()
            : Error.Field("gstRate", "Use a notified GST rate (0, 0.25, 3, 5, 12, 18, 28 or 40%).");
    }
}

/// <summary>
/// How strictly a drug is handled: its schedule (H, H1 and X need a prescription; H1 and X a register), whether it is a
/// controlled (NDPS) drug, and whether it is a high-alert medicine. On an item already in the master, any of these made
/// less strict waits for a second person (<see cref="Services.DrugClassApprovals"/>): one person unticking "controlled"
/// would otherwise switch off the NDPS register and the witness at the bedside at once. Made stricter, it applies at once.
/// </summary>
public sealed record DrugSafetyClass(DrugSchedule Schedule, bool IsControlled, bool IsHighAlert)
{
    public static DrugSafetyClass Of(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new(item.Schedule, item.IsControlled, item.IsHighAlert);
    }

    /// <summary>Whether <paramref name="next"/> handles the drug less strictly than this in any way.</summary>
    public bool IsWeakenedBy(DrugSafetyClass next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return (IsControlled && !next.IsControlled) || (IsHighAlert && !next.IsHighAlert) || Strictness(next.Schedule) < Strictness(Schedule);
    }

    public void ApplyTo(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        (item.Schedule, item.IsControlled, item.IsHighAlert) = (Schedule, IsControlled, IsHighAlert);
    }

    /// <summary>The class in words, for the approval and the audit trail: "Schedule H1, controlled (NDPS), high-alert".</summary>
    public string Words()
    {
        List<string> parts = [Schedule switch
        {
            DrugSchedule.None => "No schedule",
            DrugSchedule.Otc => "Over the counter",
            _ => "Schedule " + Schedule,
        }];
        parts.Add(IsControlled ? "controlled (NDPS)" : "not controlled");
        parts.Add(IsHighAlert ? "high-alert" : "not high-alert");
        return string.Join(", ", parts);
    }

    /// <summary>How strict a schedule is: X (kept apart, with a register) above H1 (a register) above H (a prescription) above the rest.</summary>
    internal static int Strictness(DrugSchedule schedule) => schedule switch
    {
        DrugSchedule.X => 3,
        DrugSchedule.H1 => 2,
        DrugSchedule.H => 1,
        _ => 0,
    };
}
