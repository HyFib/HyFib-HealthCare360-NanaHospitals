using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

public enum MaterialKind
{
    Implant = 0,
    Consumable,
}

/// <summary>
/// An implant or consumable used in a surgery (OT-06). Each row is billed as its own ad-hoc charge at the actual
/// price, and implants keep manufacturer/lot/serial so every patient who received a recalled device can be found.
/// The bill names the brand, maker, batch and specification of each, and never charges a unit above the MRP on its pack
/// or, for a price-controlled implant, its NPPA ceiling price.
/// </summary>
public sealed class SurgeryMaterial : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid SurgeryBookingId { get; set; }
    public Guid PatientId { get; set; }
    public MaterialKind Kind { get; set; }

    /// <summary>Item master id in SupplyChain, when the item came from the OT store.</summary>
    public Guid? SupplyItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    /// <summary>The brand: from the item master for an OT store item, as on the pack for a consignment one.</summary>
    public string? Brand { get; set; }

    /// <summary>The manufacturer, or for an imported device the importer.</summary>
    public string? Manufacturer { get; set; }

    /// <summary>What the device is, as its bill says (for a stent, bare-metal or drug-eluting first; material, size).</summary>
    public string? Specification { get; set; }

    /// <summary>The lot: as written, else the batch the OT store issued (FEFO), so the bill and a recall both have it.</summary>
    public string? LotNumber { get; set; }

    public string? SerialNumber { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }

    /// <summary>The MRP of a unit printed on the pack: the lowest of the batches issued, or as written for a consignment one.</summary>
    public decimal? Mrp { get; set; }

    /// <summary>The NPPA ceiling price of a unit, with GST, of a price-controlled implant (from the item master).</summary>
    public decimal? CeilingPrice { get; set; }

    public string? RecordedByName { get; set; }

    public ChargeStatus ChargeStatus { get; set; }
    public string? ChargeError { get; set; }

    public decimal Amount => Money.Round(Quantity * UnitPrice);

    /// <summary>What the bill says of the item (NPPA), and the most a unit may be charged.</summary>
    public DeviceDetails? Device => DeviceDetails.Of(Brand, Manufacturer, Specification, LotNumber, SerialNumber, Mrp, CeilingPrice);

    /// <summary>
    /// Traceability rule: an implant without a lot or serial number could not be recalled, and an expired implant
    /// must never be used. An implant from the OT store is recorded with the batch the store issues
    /// (<paramref name="fromStore"/>), so it needs neither written.
    /// </summary>
    public static Result Validate(MaterialKind kind, string? lotNumber, string? serialNumber, DateOnly? expiryDate, DateOnly today, bool fromStore = false)
    {
        if (kind == MaterialKind.Implant && !fromStore && string.IsNullOrWhiteSpace(lotNumber) && string.IsNullOrWhiteSpace(serialNumber))
        {
            return Error.Field("lotNumber", "Enter the lot or serial number of the implant (needed for device recalls).");
        }

        return expiryDate is { } expiry && expiry < today
            ? Error.Rule("material.expired", "This item is past its expiry date and must not be used.")
            : Result.Success();
    }

    /// <summary>
    /// A consignment or patient-supplied implant is not in the item master, so its bill details come from its pack: its
    /// brand, its manufacturer or importer, and the MRP printed on it (NPPA).
    /// </summary>
    public static Result ValidateConsignment(MaterialKind kind, string? brand, string? manufacturer, decimal? mrp)
    {
        if (kind != MaterialKind.Implant)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(brand))
        {
            return Error.Field("brand", "Enter the implant's brand, as on its pack: the bill names it (NPPA).");
        }

        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return Error.Field("manufacturer", "Enter the implant's manufacturer or importer, as on its pack: the bill names it (NPPA).");
        }

        return mrp is > 0 ? Result.Success() : Error.Field("mrp", "Enter the MRP printed on the implant's pack: no more may be charged.");
    }

    /// <summary>
    /// The price of a unit may not be above the MRP on its pack, nor a price-controlled implant's above its NPPA ceiling
    /// price (DPCO 2013), even from a pack printed before the ceiling was cut.
    /// </summary>
    public Result CheckPrice()
    {
        if (Device?.PriceLimit() is not { } limit || UnitPrice <= limit)
        {
            return Result.Success();
        }

        var what = Device.CeilingLimits() ? "its NPPA ceiling price" : "the MRP printed on its pack";
        return Error.Field("unitPrice", $"{ItemName} can be charged at most ₹{Money.FormatInr(limit)} a unit: {what}.");
    }
}
