using System.Globalization;

namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum GoodsReceiptStatus
{
    Draft = 0,
    Posted,
    Cancelled,
}

/// <summary>
/// Goods receipt note: a vendor invoice received into a store (INV-03). Posting creates or tops up the batches
/// (batch number, expiry, MRP), writes GRN ledger rows and updates the purchase order's received quantities.
/// Posted GRNs are immutable; corrections go through stock adjustments.
/// </summary>
public sealed class GoodsReceiptNote : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid VendorId { get; set; }
    public Guid StoreId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public string VendorInvoiceNumber { get; set; } = string.Empty;
    public DateOnly? VendorInvoiceDate { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public bool IsInterState { get; set; }
    public string? Remarks { get; set; }
    public GoodsReceiptStatus Status { get; private set; }
    public DateTimeOffset? PostedAt { get; private set; }
    public Guid? PostedByUserId { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public decimal GrossAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }
    public decimal IgstAmount { get; private set; }
    public decimal RoundOff { get; private set; }
    public decimal NetAmount { get; private set; }
    public List<GoodsReceiptLine> Lines { get; set; } = [];

    public void Recalculate()
    {
        foreach (var line in Lines)
        {
            line.Price(IsInterState);
        }

        GrossAmount = Lines.Sum(l => l.GrossAmount);
        DiscountAmount = Lines.Sum(l => l.DiscountAmount);
        TaxableAmount = Lines.Sum(l => l.TaxableAmount);
        CgstAmount = Lines.Sum(l => l.CgstAmount);
        SgstAmount = Lines.Sum(l => l.SgstAmount);
        IgstAmount = Lines.Sum(l => l.IgstAmount);
        (NetAmount, RoundOff) = GstCalculator.RoundToRupee(TaxableAmount + CgstAmount + SgstAmount + IgstAmount);
    }

    public Result Validate()
    {
        if (Lines.Count == 0)
        {
            return Error.Rule("grn.empty", "Add at least one received item.");
        }

        foreach (var line in Lines)
        {
            var result = line.Validate(ReceivedOn);
            if (result.IsFailure)
            {
                return result;
            }
        }

        return Result.Success();
    }

    public Result MarkPosted(Guid? userId, DateTimeOffset now)
    {
        if (Status != GoodsReceiptStatus.Draft)
        {
            return Error.Rule("grn.not_draft", $"GRN {Number} is already {Status}.");
        }

        var valid = Validate();
        if (valid.IsFailure)
        {
            return valid;
        }

        Status = GoodsReceiptStatus.Posted;
        PostedAt = now;
        PostedByUserId = userId;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status != GoodsReceiptStatus.Draft)
        {
            return Error.Rule("grn.not_draft", "Only draft GRNs can be cancelled; posted stock is corrected with an adjustment.");
        }

        Status = GoodsReceiptStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        return Result.Success();
    }
}

public sealed class GoodsReceiptLine : Entity
{
    /// <summary>Goods expiring sooner than this after receipt need an explicit short-expiry acceptance.</summary>
    public const int ShortExpiryDays = 90;

    public Guid GoodsReceiptNoteId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid? PurchaseOrderLineId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ManufactureDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>Invoiced quantity in purchase units.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Free (scheme) quantity in purchase units — adds stock but no cost.</summary>
    public decimal FreeQuantity { get; set; }

    /// <summary>Rate per purchase unit, excluding GST.</summary>
    public decimal Rate { get; set; }

    /// <summary>MRP printed on one purchase unit (GST inclusive).</summary>
    public decimal Mrp { get; set; }

    public decimal DiscountPercent { get; set; }
    public decimal GstRate { get; set; }

    /// <summary>Snapshot of the item's conversion factor at receipt.</summary>
    public decimal UnitsPerPurchaseUnit { get; set; } = 1;

    /// <summary>Snapshot: batch and expiry are mandatory for this item type.</summary>
    public bool RequiresBatch { get; set; }

    /// <summary>Snapshot: MRP is mandatory (medicines under DPCO).</summary>
    public bool RequiresMrp { get; set; }

    public bool AcceptShortExpiry { get; set; }
    public decimal GrossAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }
    public decimal IgstAmount { get; private set; }
    public decimal LineTotal { get; private set; }
    public Guid? StockBatchId { get; internal set; }

    /// <summary>Stock added, in base units (paid + free).</summary>
    public decimal BaseQuantity => (Quantity + FreeQuantity) * UnitsPerPurchaseUnit;

    public decimal UnitMrp => UnitsPerPurchaseUnit <= 0 ? 0 : Math.Round(Mrp / UnitsPerPurchaseUnit, 4, MidpointRounding.AwayFromZero);

    /// <summary>Cost per base unit: the taxable value spread over paid and free units (free goods lower the unit cost).</summary>
    public decimal UnitCost => BaseQuantity <= 0 ? 0 : Math.Round(TaxableAmount / BaseQuantity, 4, MidpointRounding.AwayFromZero);

    internal void Price(bool interState)
    {
        var priced = GstCalculator.PriceExclusive(Quantity, Rate, DiscountPercent, GstRate, interState);
        GrossAmount = priced.Gross;
        DiscountAmount = priced.Discount;
        TaxableAmount = priced.Tax.Taxable;
        CgstAmount = priced.Tax.Cgst;
        SgstAmount = priced.Tax.Sgst;
        IgstAmount = priced.Tax.Igst;
        LineTotal = priced.Net;
    }

    /// <summary>Receiving rules: never accept expired goods, confirm short expiry, rate within MRP, batch/expiry present.</summary>
    public Result Validate(DateOnly receivedOn)
    {
        if (Quantity < 0 || FreeQuantity < 0 || Quantity + FreeQuantity <= 0)
        {
            return Error.Rule("grn.invalid_quantity", $"{ItemName}: enter the received quantity.");
        }

        if (UnitsPerPurchaseUnit <= 0)
        {
            return Error.Rule("grn.invalid_conversion", $"{ItemName}: the item has no valid purchase-unit conversion.");
        }

        if (RequiresBatch && string.IsNullOrWhiteSpace(BatchNumber))
        {
            return Error.Rule("grn.batch_required", $"{ItemName}: batch number is required.");
        }

        if (RequiresBatch && ExpiryDate is null)
        {
            return Error.Rule("grn.expiry_required", $"{ItemName}: expiry date is required.");
        }

        if (ExpiryDate is { } expiry)
        {
            if (expiry <= receivedOn)
            {
                return Error.Rule("grn.expired", $"{ItemName} batch {BatchNumber} expired on {Format(expiry)}. Do not accept expired goods.");
            }

            if (expiry < receivedOn.AddDays(ShortExpiryDays) && !AcceptShortExpiry)
            {
                return Error.Rule("grn.short_expiry", $"{ItemName} batch {BatchNumber} expires on {Format(expiry)}, within {ShortExpiryDays} days. Confirm short-expiry acceptance to receive it.");
            }

            if (ManufactureDate is { } manufactured && manufactured >= expiry)
            {
                return Error.Rule("grn.invalid_dates", $"{ItemName}: the manufacturing date must be before the expiry date.");
            }
        }

        if (RequiresMrp && Mrp <= 0)
        {
            return Error.Rule("grn.mrp_required", $"{ItemName}: enter the MRP printed on the pack.");
        }

        if (Mrp > 0 && Rate > Mrp)
        {
            return Error.Rule("grn.rate_above_mrp", $"{ItemName}: purchase rate {StockErrors.Format(Rate)} is above the MRP {StockErrors.Format(Mrp)}.");
        }

        return Result.Success();
    }

    private static string Format(DateOnly date) => date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
}
