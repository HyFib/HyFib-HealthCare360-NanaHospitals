using System.Globalization;

namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>Stock rule failures with stable codes the UI can react to (for example "stock.insufficient").</summary>
public static class StockErrors
{
    public static Error InvalidQuantity { get; } = Error.Rule("stock.invalid_quantity", "Quantity must be more than zero.");

    /// <summary>
    /// Raised when <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> shows that a batch changed
    /// between reading and saving (optimistic concurrency on <c>xmin</c>). Retrying with fresh data is always safe.
    /// </summary>
    public static Error ConcurrentUpdate { get; } = Error.Conflict(
        "stock.concurrent_update",
        "Stock changed while you were working (another counter issued or received the same item). Please retry.");

    public static Error Insufficient(string? itemName, decimal available, decimal requested) =>
        new Error(
            "stock.insufficient",
            $"Not enough stock{(itemName is null ? string.Empty : " of " + itemName)}: {Format(available)} available, {Format(requested)} requested.")
        {
            Data = new { itemName, available, requested },
        };

    public static Error Expired(string batchNumber, DateOnly? expiry) =>
        Error.Rule("stock.batch_expired", $"Batch {batchNumber} expired on {FormatDate(expiry)}. Expired stock can only be written off.");

    public static Error ShortExpiry(string batchNumber, DateOnly? expiry) =>
        Error.Rule("stock.batch_short_expiry", $"Batch {batchNumber} expires on {FormatDate(expiry)}, before this supply would be used up.");

    public static Error Blocked(string batchNumber, string? reason) =>
        Error.Rule("stock.batch_blocked", $"Batch {batchNumber} is blocked{(string.IsNullOrWhiteSpace(reason) ? string.Empty : " (" + reason + ")")} and cannot be issued.");

    public static Error WrongBatch { get; } = Error.Rule("stock.wrong_batch", "The selected batch does not belong to this item and store.");

    public static Error LotNotFound(string batchNumber) => Error.Rule(
        "stock.batch_not_found",
        $"There is no stock of batch {batchNumber} in this store. Check the lot number on the pack, or receive the batch first.");

    public static string Format(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly? date) => date?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? "-";
}

/// <summary>
/// Stock of one batch of an item in one store, in the item's base unit. Quantities change only through the
/// methods below, which keep the invariants <c>0 ≤ reserved ≤ on hand</c> (also enforced by a database check
/// constraint). Concurrent updates are detected with the <c>xmin</c> <see cref="AuditableEntity.Version"/>; the
/// ledger (<see cref="StockLedgerEntry"/>) is the movement audit trail, so batch rows are not data-audited.
/// </summary>
[NotAudited]
public sealed class StockBatch : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>Last day the batch may be used; month-only expiries ("EXP 03/2027") are stored as the month's last day.</summary>
    public DateOnly? ExpiryDate { get; set; }

    public DateOnly? ManufactureDate { get; set; }

    /// <summary>Maximum retail price per base unit (GST inclusive), derived from the pack MRP on the GRN.</summary>
    public decimal Mrp { get; set; }

    /// <summary>Cost per base unit net of trade discount and free goods, excluding GST (weighted average on re-receipt).</summary>
    public decimal PurchaseRate { get; set; }

    public decimal GstRate { get; set; }
    public decimal QuantityOnHand { get; private set; }

    /// <summary>Set aside for an approved indent or booked surgery; not available to other issues.</summary>
    public decimal QuantityReserved { get; private set; }

    public Guid? VendorId { get; set; }
    public Guid? GoodsReceiptId { get; set; }
    public DateOnly ReceivedOn { get; set; }

    /// <summary>Quality hold or recall: never allocated until unblocked.</summary>
    public bool IsBlocked { get; private set; }

    public string? BlockReason { get; private set; }

    public decimal QuantityAvailable => QuantityOnHand - QuantityReserved;

    /// <summary>Expired once the expiry day has passed (the expiry day itself is still usable).</summary>
    public bool IsExpired(DateOnly today) => ExpiryDate is { } expiry && expiry < today;

    public Result EnsureIssuable(DateOnly today)
    {
        if (IsBlocked)
        {
            return StockErrors.Blocked(BatchNumber, BlockReason);
        }

        return IsExpired(today) ? StockErrors.Expired(BatchNumber, ExpiryDate) : Result.Success();
    }

    /// <summary>
    /// Adds stock (GRN, transfer-in, return). A receipt at a different cost re-averages the batch cost so stock
    /// valuation stays correct when the same batch arrives on two invoices.
    /// </summary>
    public Result Receive(decimal quantity, decimal unitCost)
    {
        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (unitCost < 0)
        {
            return Error.Rule("stock.invalid_cost", "Unit cost cannot be negative.");
        }

        var total = QuantityOnHand + quantity;
        PurchaseRate = QuantityOnHand <= 0
            ? unitCost
            : Math.Round(((QuantityOnHand * PurchaseRate) + (quantity * unitCost)) / total, 4, MidpointRounding.AwayFromZero);
        QuantityOnHand = total;
        return Result.Success();
    }

    /// <summary>Takes unreserved stock out (issue, sale, dispense, transfer-out, consumption).</summary>
    public Result Deduct(decimal quantity)
    {
        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (quantity > QuantityAvailable)
        {
            return StockErrors.Insufficient($"batch {BatchNumber}", QuantityAvailable, quantity);
        }

        QuantityOnHand -= quantity;
        return Result.Success();
    }

    public Result Reserve(decimal quantity)
    {
        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (quantity > QuantityAvailable)
        {
            return StockErrors.Insufficient($"batch {BatchNumber}", QuantityAvailable, quantity);
        }

        QuantityReserved += quantity;
        return Result.Success();
    }

    public Result ReleaseReservation(decimal quantity)
    {
        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (quantity > QuantityReserved)
        {
            return Error.Rule("stock.reservation_exceeded", $"Only {StockErrors.Format(QuantityReserved)} of batch {BatchNumber} is reserved.");
        }

        QuantityReserved -= quantity;
        return Result.Success();
    }

    /// <summary>Issues stock that was reserved earlier: the reservation turns into an issue.</summary>
    public Result ConsumeReservation(decimal quantity)
    {
        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (quantity > QuantityReserved)
        {
            return Error.Rule("stock.reservation_exceeded", $"Only {StockErrors.Format(QuantityReserved)} of batch {BatchNumber} is reserved.");
        }

        QuantityReserved -= quantity;
        QuantityOnHand -= quantity;
        return Result.Success();
    }

    /// <summary>
    /// Approved correction (count variance, breakage, expiry write-off). Expired stock may be written off, but
    /// reserved stock must be released first so an approved indent is never silently emptied.
    /// </summary>
    public Result Adjust(decimal delta)
    {
        if (delta == 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (delta < 0 && -delta > QuantityAvailable)
        {
            return StockErrors.Insufficient($"batch {BatchNumber}", QuantityAvailable, -delta);
        }

        QuantityOnHand += delta;
        return Result.Success();
    }

    public Result Block(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Give a reason for blocking the batch (recall, quality hold…).");
        }

        IsBlocked = true;
        BlockReason = reason.Trim();
        return Result.Success();
    }

    public void Unblock()
    {
        IsBlocked = false;
        BlockReason = null;
    }
}
