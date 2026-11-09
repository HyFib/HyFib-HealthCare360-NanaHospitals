using System.Globalization;

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

/// <summary>
/// The list of the Telemedicine Practice Guidelines 2020 (Board of Governors in supersession of the MCI, 25 March 2020,
/// section 3.7.4 and Annexure 2) a medicine is on, which decides whether a doctor may prescribe it in a teleconsultation.
/// Which medicine goes on which list is the pharmacy and therapeutics committee's decision, so nothing but the Prohibited
/// List is filled in for the hospital: a medicine on no list is not prescribed in a first teleconsultation.
/// </summary>
public enum TelemedicineList
{
    /// <summary>List O: safe medicines (common over-the-counter ones), prescribed in any teleconsultation.</summary>
    O = 0,

    /// <summary>
    /// List A: relatively safe medicines with low potential for abuse, prescribed in a first teleconsultation only when it is
    /// by video, and re-prescribed in a follow-up.
    /// </summary>
    A,

    /// <summary>
    /// List B: medicines added on in a follow-up teleconsultation, for a condition the doctor saw the patient in person for.
    /// </summary>
    B,

    /// <summary>The Prohibited List: never in a teleconsultation (Schedule X of the Drugs and Cosmetics Rules, and NDPS drugs).</summary>
    Prohibited,
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

    /// <summary>
    /// The Telemedicine Practice Guidelines' list the pharmacy and therapeutics committee put the medicine on; null until it
    /// has, which keeps it out of a first teleconsultation. A Schedule X or NDPS medicine is on the Prohibited List whatever
    /// this says (<see cref="TelemedicineLists"/>).
    /// </summary>
    public TelemedicineList? TelemedicineList { get; set; }

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
    /// The NPPA ceiling price of a base unit (a tablet, a ml, a stent) with the GST on it. For a price-controlled implant (a
    /// coronary stent, a knee implant) no patient is charged more, whatever the MRP printed on an older pack. For a scheduled
    /// formulation under the Drugs (Prices Control) Order 2013 no pack is received with an MRP above it, and none is sold
    /// above it (<see cref="PriceCeiling"/>). NPPA revises it, so the store updates it; raising or removing it, or changing
    /// the GST rate, waits for a second person (<see cref="Services.ItemPriceApprovals"/>).
    /// </summary>
    public decimal? CeilingPrice { get; set; }

    /// <summary>The NPPA notification that fixed <see cref="CeilingPrice"/> (its S.O. number and date), printed with refusals.</summary>
    public string? CeilingNotification { get; set; }

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

        if (TelemedicineList is not null && Type != ItemType.Drug)
        {
            return Error.Field("telemedicineList", "Only a medicine is put on a list of the Telemedicine Practice Guidelines.");
        }

        if (TelemedicineLists.ByLaw(Schedule, IsControlled) && TelemedicineList is not (null or TelemedicineLists.Prohibited))
        {
            return Error.Field("telemedicineList",
                "A Schedule X or NDPS medicine is on the Prohibited List of the Telemedicine Practice Guidelines and is never prescribed in a teleconsultation. Choose the Prohibited List.");
        }

        if (CeilingPrice is not null && (Type is not (ItemType.Implant or ItemType.Drug) || CeilingPrice <= 0))
        {
            return Error.Field("ceilingPrice",
                "An NPPA ceiling price is set on a scheduled medicine (DPCO 2013) or a price-controlled implant (a coronary stent, a knee implant), and is more than zero.");
        }

        return GstCalculator.IsStandardRate(GstRate)
            ? Result.Success()
            : Error.Field("gstRate", "Use a notified GST rate (0, 0.25, 3, 5, 12, 18, 28 or 40%).");
    }
}

/// <summary>
/// The Drugs (Prices Control) Order 2013 for a scheduled formulation: NPPA fixes a ceiling price per unit (a tablet, a
/// capsule, a ml) and no one sells it above that, GST added (para 14 and 20). The ceiling is kept per base unit with GST
/// on <see cref="Item.CeilingPrice"/>; a pack's ceiling is that times the units in the pack, rounded to the paisa, so a
/// pack priced at the ceiling is never refused for the rounding of its per-unit MRP. Implants are charged at the lower of
/// their MRP and ceiling instead (an older pack printed before a cut is still used), so this applies to medicines only.
/// </summary>
public static class PriceCeiling
{
    /// <summary>A sale or receipt above the ceiling of a scheduled medicine.</summary>
    public const string SaleCode = "pharmacy.price_above_ceiling";

    public const string ReceiptCode = "grn.mrp_above_ceiling";

    /// <summary>Whether the item is a price-controlled medicine (an implant's ceiling caps the charge instead).</summary>
    public static bool AppliesTo(ItemType type, decimal? ceiling) => type == ItemType.Drug && ceiling is > 0;

    /// <summary>The ceiling of a pack of <paramref name="units"/> base units, with GST, to the paisa.</summary>
    public static decimal PerPack(decimal ceilingPerUnit, decimal units) =>
        Math.Round(ceilingPerUnit * units, 2, MidpointRounding.AwayFromZero);

    /// <summary>Whether a pack's MRP is above the ceiling of its <paramref name="units"/> base units.</summary>
    public static bool PackExceeds(decimal packMrp, decimal ceilingPerUnit, decimal units) =>
        Math.Round(packMrp, 2, MidpointRounding.AwayFromZero) > PerPack(ceilingPerUnit, units);

    /// <summary>
    /// Whether a batch's MRP per base unit is above the ceiling: compared as a pack of the item's purchase unit, as it was
    /// printed, so a per-unit MRP rounded to four places (₹23.46 a strip of 10 is ₹2.346 a tablet) compares as received.
    /// </summary>
    public static bool UnitExceeds(decimal unitMrp, decimal ceilingPerUnit, decimal unitsPerPack) =>
        PackExceeds(unitMrp * (unitsPerPack > 0 ? unitsPerPack : 1), ceilingPerUnit, unitsPerPack > 0 ? unitsPerPack : 1);

    /// <summary>The refusal of a sale or issue of a batch priced above the ceiling.</summary>
    public static Error SaleRefusal(Guid itemId, string itemName, string batchNumber, decimal unitMrp, decimal ceilingPerUnit, string baseUnit, string? notification) =>
        new(SaleCode,
            $"{itemName} batch {batchNumber} has an MRP of {Money(unitMrp)} a {baseUnit.ToLowerInvariant()}, above its NPPA ceiling price of {Money(ceilingPerUnit)}"
            + (string.IsNullOrWhiteSpace(notification) ? "" : $" ({notification})")
            + ". It cannot be sold above the ceiling (DPCO 2013): choose another batch, or ask the store to return it to the supplier.")
        {
            Data = new { itemId, batchNumber, unitMrp, ceilingPrice = ceilingPerUnit },
        };

    internal static string Money(decimal amount) => "₹" + amount.ToString(amount == Math.Round(amount, 2) ? "0.00" : "0.00##", CultureInfo.InvariantCulture);
}

/// <summary>
/// What an item is priced at by law and taxed at: its NPPA ceiling price (per base unit, with GST) and notification, and its
/// GST rate. On an item already in the master, a ceiling raised or removed, or a GST rate changed, waits for a second person
/// who approves prices (<see cref="Services.ItemPriceApprovals"/>); a ceiling set or lowered applies at once.
/// </summary>
public sealed record ItemPricing(decimal? CeilingPrice, string? CeilingNotification, decimal GstRate)
{
    public static ItemPricing Of(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new(item.CeilingPrice, item.CeilingNotification, item.GstRate);
    }

    /// <summary>Whether <paramref name="next"/> loosens the price control: a higher or no ceiling, or another GST rate.</summary>
    public bool IsLoosenedBy(ItemPricing next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return GstRate != next.GstRate || (CeilingPrice is { } ceiling && (next.CeilingPrice is not { } after || after > ceiling));
    }

    public void ApplyTo(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        (item.CeilingPrice, item.CeilingNotification, item.GstRate) = (CeilingPrice, CeilingNotification, GstRate);
    }

    /// <summary>In words, for the approval and the audit: "Ceiling ₹2.35 a TAB (₹23.50 a STRIP of 10), with GST; GST 12%".</summary>
    public string Words(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var gst = "GST " + GstRate.ToString("0.##", CultureInfo.InvariantCulture) + "%";
        if (CeilingPrice is not { } ceiling)
        {
            return "No ceiling price; " + gst;
        }

        var pack = item.PurchaseUnitFactor > 1
            ? $" ({PriceCeiling.Money(PriceCeiling.PerPack(ceiling, item.PurchaseUnitFactor))} a {item.PurchaseUnit} of {item.PurchaseUnitFactor.ToString("0.##", CultureInfo.InvariantCulture)})"
            : "";
        var notified = string.IsNullOrWhiteSpace(CeilingNotification) ? "" : $", {CeilingNotification}";
        return $"Ceiling {PriceCeiling.Money(ceiling)} a {item.BaseUnit}{pack} with GST{notified}; {gst}";
    }
}

/// <summary>
/// How strictly a drug is handled: its schedule (H, H1 and X need a prescription; H1 and X a register), whether it is a
/// controlled (NDPS) drug, and whether it is a high-alert medicine. On an item already in the master, any of these made
/// less strict waits for a second person (<see cref="Services.DrugClassApprovals"/>): one person unticking "controlled"
/// would otherwise switch off the NDPS register and the witness at the bedside at once. Made stricter, it applies at once.
/// The Telemedicine Practice Guidelines' list is part of it: moved to a list that lets the medicine into more
/// teleconsultations (from no list to List B, from List A to List O), it waits for the pharmacist too.
/// </summary>
public sealed record DrugSafetyClass(DrugSchedule Schedule, bool IsControlled, bool IsHighAlert, TelemedicineList? TelemedicineList = null)
{
    public static DrugSafetyClass Of(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new(item.Schedule, item.IsControlled, item.IsHighAlert, item.TelemedicineList);
    }

    /// <summary>Whether <paramref name="next"/> handles the drug less strictly than this in any way.</summary>
    public bool IsWeakenedBy(DrugSafetyClass next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return (IsControlled && !next.IsControlled) || (IsHighAlert && !next.IsHighAlert) || Strictness(next.Schedule) < Strictness(Schedule)
               || TelemedicineLists.Strictness(next.TelemedicineList) < TelemedicineLists.Strictness(TelemedicineList);
    }

    public void ApplyTo(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        (item.Schedule, item.IsControlled, item.IsHighAlert, item.TelemedicineList) = (Schedule, IsControlled, IsHighAlert, TelemedicineList);
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
        if (TelemedicineList is { } list)
        {
            parts.Add(TelemedicineLists.Words(list));
        }

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

/// <summary>The Telemedicine Practice Guidelines' lists (<see cref="TelemedicineList"/>): what the law puts on one, and how strict each is.</summary>
public static class TelemedicineLists
{
    /// <summary>The Prohibited List, named where the property of the same name hides the enum.</summary>
    public const TelemedicineList Prohibited = Domain.TelemedicineList.Prohibited;

    /// <summary>A Schedule X or NDPS medicine, which the guidelines put on the Prohibited List whatever the item master says.</summary>
    public static bool ByLaw(DrugSchedule schedule, bool isControlled) => isControlled || schedule == DrugSchedule.X;

    /// <summary>
    /// How few teleconsultations a list lets the medicine into: the Prohibited List none; no list (the committee has not
    /// decided) only a follow-up, as List B; List B only a follow-up, as an add-on; List A a video first one too; List O all.
    /// Unlisted ranks above List B, so putting a medicine on a list for the first time waits for a pharmacist.
    /// </summary>
    internal static int Strictness(TelemedicineList? list) => list switch
    {
        Domain.TelemedicineList.Prohibited => 4,
        null => 3,
        Domain.TelemedicineList.B => 2,
        Domain.TelemedicineList.A => 1,
        _ => 0,
    };

    public static string Words(TelemedicineList list) => list switch
    {
        Domain.TelemedicineList.Prohibited => "on the telemedicine Prohibited List",
        _ => "on telemedicine List " + list,
    };
}
