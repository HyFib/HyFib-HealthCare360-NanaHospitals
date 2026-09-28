namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum AdjustmentReason
{
    Damage = 0,
    Breakage,
    Expiry,
    Loss,
    Theft,
    CountVariance,
    DataCorrection,
    Other,
}

public enum AdjustmentStatus
{
    PendingApproval = 0,
    Approved,
    Rejected,
}

/// <summary>
/// Stock correction with a reason that changes stock only after approval (INV-07 stock audit). The approver must
/// be a different person from the requester (segregation of duties for write-offs). Expiry write-offs post as
/// <see cref="StockMovementType.ExpiryWriteOff"/>, everything else as <see cref="StockMovementType.Adjustment"/>.
/// </summary>
public sealed class StockAdjustment : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid StoreId { get; set; }
    public AdjustmentReason Reason { get; set; }
    public string? Remarks { get; set; }
    public AdjustmentStatus Status { get; private set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? RejectReason { get; private set; }

    /// <summary>Set when the adjustment came from a physical stock count.</summary>
    public Guid? PhysicalCountId { get; set; }

    /// <summary>Signed value at cost (negative for write-offs).</summary>
    public decimal NetValue { get; set; }

    public List<StockAdjustmentLine> Lines { get; set; } = [];

    public StockMovementType MovementType => Reason == AdjustmentReason.Expiry ? StockMovementType.ExpiryWriteOff : StockMovementType.Adjustment;

    public Result ValidateLines()
    {
        if (Lines.Count == 0)
        {
            return Error.Rule("adjustment.empty", "Add at least one batch to adjust.");
        }

        if (Lines.Any(l => l.QuantityChange == 0))
        {
            return Error.Rule("adjustment.zero_line", "Each line must change the quantity.");
        }

        var writeOffOnly = Reason is AdjustmentReason.Expiry or AdjustmentReason.Damage or AdjustmentReason.Breakage or AdjustmentReason.Loss or AdjustmentReason.Theft;
        if (writeOffOnly && Lines.Any(l => l.QuantityChange > 0))
        {
            return Error.Rule("adjustment.reason_mismatch", $"A {Reason} adjustment can only reduce stock.");
        }

        return Result.Success();
    }

    public Result Approve(Guid? approverId, DateTimeOffset now)
    {
        if (Status != AdjustmentStatus.PendingApproval)
        {
            return Error.Rule("adjustment.decided", $"Adjustment {Number} is already {Status}.");
        }

        if (approverId is not null && approverId == RequestedByUserId)
        {
            return Error.Forbidden("adjustment.self_approval", "An adjustment must be approved by someone other than the person who raised it.");
        }

        Status = AdjustmentStatus.Approved;
        DecidedByUserId = approverId;
        DecidedAt = now;
        return Result.Success();
    }

    public Result Reject(Guid? approverId, string reason, DateTimeOffset now)
    {
        if (Status != AdjustmentStatus.PendingApproval)
        {
            return Error.Rule("adjustment.decided", $"Adjustment {Number} is already {Status}.");
        }

        Status = AdjustmentStatus.Rejected;
        DecidedByUserId = approverId;
        DecidedAt = now;
        RejectReason = reason;
        return Result.Success();
    }
}

public sealed class StockAdjustmentLine : Entity
{
    public Guid StockAdjustmentId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>Book quantity when the adjustment was raised (for the approver's context).</summary>
    public decimal SystemQuantity { get; set; }

    /// <summary>Signed change in base units: negative writes off, positive books found stock.</summary>
    public decimal QuantityChange { get; set; }

    public decimal UnitCost { get; set; }
    public decimal Value { get; set; }
}

public enum StockCountStatus
{
    InProgress = 0,
    Submitted,
    Adjusted,
    Cancelled,
}

/// <summary>
/// Physical stock count (stock audit, INV-07): a count sheet of the store's batches is generated, counters enter
/// physical quantities, and submitting compares them with the book quantity at that moment. Non-zero variances
/// become one <see cref="StockAdjustment"/> (reason <see cref="AdjustmentReason.CountVariance"/>) awaiting approval.
/// Count while the store is closed (or per rack) so the book quantity at submission matches what was counted.
/// </summary>
public sealed class PhysicalStockCount : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid StoreId { get; set; }
    public StockCountStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; set; }
    public Guid? StartedByUserId { get; set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public ItemType? ItemTypeFilter { get; set; }
    public Guid? CategoryFilterId { get; set; }
    public string? Remarks { get; set; }
    public Guid? AdjustmentId { get; private set; }
    public List<StockCountLine> Lines { get; set; } = [];

    public Result RecordCount(Guid lineId, decimal countedQuantity, string? remarks)
    {
        if (Status != StockCountStatus.InProgress)
        {
            return Error.Rule("count.closed", $"Count {Number} is {Status}.");
        }

        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("StockCountLine", lineId);
        }

        if (countedQuantity < 0)
        {
            return Error.Rule("count.negative", "Counted quantity cannot be negative.");
        }

        line.CountedQuantity = countedQuantity;
        line.Remarks = string.IsNullOrWhiteSpace(remarks) ? line.Remarks : remarks.Trim();
        return Result.Success();
    }

    /// <summary>Freezes the variances against the current book quantities (keyed by batch id).</summary>
    public Result Submit(IReadOnlyDictionary<Guid, decimal> bookQuantities, DateTimeOffset now)
    {
        if (Status != StockCountStatus.InProgress)
        {
            return Error.Rule("count.closed", $"Count {Number} is {Status}.");
        }

        if (Lines.All(l => l.CountedQuantity is null))
        {
            return Error.Rule("count.nothing_counted", "Enter at least one counted quantity before submitting.");
        }

        foreach (var line in Lines.Where(l => l.CountedQuantity is not null))
        {
            var book = bookQuantities.GetValueOrDefault(line.BatchId);
            line.VarianceQuantity = line.CountedQuantity!.Value - book;
        }

        Status = StockCountStatus.Submitted;
        SubmittedAt = now;
        return Result.Success();
    }

    public void LinkAdjustment(Guid adjustmentId) => AdjustmentId = adjustmentId;

    /// <summary>Marks the count finished: its variance adjustment was approved (or there was nothing to adjust).</summary>
    public void MarkAdjusted() => Status = StockCountStatus.Adjusted;

    /// <summary>Re-opens a submitted count whose variance adjustment was rejected, so the store can recount.</summary>
    public void ReopenForRecount()
    {
        if (Status == StockCountStatus.Submitted)
        {
            Status = StockCountStatus.InProgress;
            AdjustmentId = null;
        }
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (Status != StockCountStatus.InProgress)
        {
            return Error.Rule("count.closed", $"Count {Number} is {Status}.");
        }

        Status = StockCountStatus.Cancelled;
        SubmittedAt = now;
        return Result.Success();
    }
}

public sealed class StockCountLine : Entity
{
    public Guid PhysicalStockCountId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>Book quantity when the sheet was generated (printed on the count sheet).</summary>
    public decimal SystemQuantity { get; set; }

    public decimal? CountedQuantity { get; internal set; }
    public decimal? VarianceQuantity { get; internal set; }
    public decimal UnitCost { get; set; }
    public string? Remarks { get; internal set; }
}
