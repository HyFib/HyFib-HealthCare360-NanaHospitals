namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum TransferStatus
{
    InTransit = 0,
    Received,
    Cancelled,
}

/// <summary>
/// Stock transfer note between two stores of a facility (INV-06, PH-04). Dispatch takes the stock out of the
/// source (transfer-out) and the goods are "in transit" until the receiving store confirms, which books them in
/// under the same batch, expiry and MRP. Cancelling an in-transit transfer puts the stock back.
/// </summary>
public sealed class StockTransfer : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid FromStoreId { get; set; }
    public Guid ToStoreId { get; set; }
    public TransferStatus Status { get; private set; }
    public DateTimeOffset DispatchedAt { get; set; }
    public Guid? DispatchedByUserId { get; set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public Guid? ReceivedByUserId { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public string? Remarks { get; set; }
    public decimal TotalValue { get; set; }
    public List<StockTransferLine> Lines { get; set; } = [];

    /// <summary>Whoever dispatched a transfer received it, or decided a shortfall on it.</summary>
    public const string ReceiveOwnCode = "transfer.receive_own";

    public const string ShortfallOwnCode = "transfer.shortfall_own";

    public bool IsDispatcher(Guid? userId) => userId is { } user && DispatchedByUserId == user;

    /// <summary>
    /// Refuses whoever dispatched the transfer: the goods are counted in at the receiving store by someone else, so a
    /// transfer note cannot be made to say it arrived by the person who sent it (MAKER-CHECKER slice 13).
    /// </summary>
    public Result CanBeReceivedBy(Guid? userId)
    {
        if (Status != TransferStatus.InTransit)
        {
            return Error.Rule("transfer.not_in_transit", $"Transfer {Number} is already {Status}.");
        }

        return IsDispatcher(userId)
            ? Error.Forbidden(ReceiveOwnCode, $"You dispatched transfer {Number}: someone at the receiving store receives it.")
            : Result.Success();
    }

    public Result Receive(Guid? userId, DateTimeOffset now)
    {
        var allowed = CanBeReceivedBy(userId);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = TransferStatus.Received;
        ReceivedAt = now;
        ReceivedByUserId = userId;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status != TransferStatus.InTransit)
        {
            return Error.Rule("transfer.not_in_transit", $"Transfer {Number} is already {Status} and cannot be cancelled.");
        }

        Status = TransferStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        return Result.Success();
    }
}

/// <summary>One batch moving between stores (a requested item split across batches gives several lines).</summary>
public sealed class StockTransferLine : Entity
{
    public Guid StockTransferId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid SourceBatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public DateOnly? ManufactureDate { get; set; }
    public decimal Mrp { get; set; }
    public decimal PurchaseRate { get; set; }
    public decimal GstRate { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>What the receiving store counted in, when it was less than was sent (null: received in full).</summary>
    public decimal? ReceivedQuantity { get; set; }
    public Guid? DestinationBatchId { get; set; }
}
