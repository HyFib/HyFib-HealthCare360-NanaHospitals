namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum IndentStatus
{
    Pending = 0,
    Approved,
    PartiallyIssued,
    Issued,
    Rejected,
    Cancelled,

    /// <summary>Short-closed after a partial issue; the balance will not be supplied.</summary>
    Closed,
}

public enum IndentPriority
{
    Routine = 0,
    Urgent,
}

/// <summary>
/// Internal requisition from a department or sub-store to an issuing store (INV-05). Approval reserves stock so
/// other issues cannot take it; issuing either transfers stock to the requesting sub-store (<see cref="ToStoreId"/>)
/// or books it as consumed by the department (<see cref="DepartmentId"/>). Quantities are in base units.
/// </summary>
public sealed class Indent : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;

    /// <summary>Store that issues the goods (usually the central store or pharmacy).</summary>
    public Guid FromStoreId { get; set; }

    /// <summary>Sub-store that receives the goods (ICU store, OT store…); stock moves as a transfer.</summary>
    public Guid? ToStoreId { get; set; }

    /// <summary>Department that consumes the goods directly when there is no receiving store.</summary>
    public Guid? DepartmentId { get; set; }

    public IndentPriority Priority { get; set; }
    public IndentStatus Status { get; private set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string? Remarks { get; set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionNote { get; private set; }

    /// <summary>Touched by every issue so concurrent issues against one indent conflict instead of over-issuing.</summary>
    public DateTimeOffset? LastIssuedAt { get; private set; }

    public List<IndentLine> Lines { get; set; } = [];

    public bool IsOpen => Status is IndentStatus.Pending or IndentStatus.Approved or IndentStatus.PartiallyIssued;

    /// <summary>Approves the requested (or reduced) quantities.</summary>
    public Result Approve(Guid? userId, IReadOnlyDictionary<Guid, decimal> approvedQuantities, DateTimeOffset now)
    {
        if (Status != IndentStatus.Pending)
        {
            return Error.Rule("indent.not_pending", "Only pending indents can be approved.");
        }

        foreach (var line in Lines)
        {
            if (approvedQuantities.TryGetValue(line.Id, out var approved))
            {
                if (approved < 0 || approved > line.RequestedQuantity)
                {
                    return Error.Rule("indent.invalid_approval", $"{line.ItemName}: approve between 0 and {StockErrors.Format(line.RequestedQuantity)}.");
                }

                line.ApprovedQuantity = approved;
            }
            else
            {
                line.ApprovedQuantity = line.RequestedQuantity;
            }
        }

        if (Lines.All(l => l.ApprovedQuantity == 0))
        {
            return Error.Rule("indent.nothing_approved", "Approve at least one line, or reject the indent.");
        }

        Status = IndentStatus.Approved;
        DecidedByUserId = userId;
        DecidedAt = now;
        return Result.Success();
    }

    public Result Reject(Guid? userId, string reason, DateTimeOffset now)
    {
        if (Status is not (IndentStatus.Pending or IndentStatus.Approved))
        {
            return Error.Rule("indent.not_rejectable", "Only pending or approved indents with nothing issued can be rejected.");
        }

        Status = IndentStatus.Rejected;
        DecidedByUserId = userId;
        DecidedAt = now;
        DecisionNote = reason;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status is not (IndentStatus.Pending or IndentStatus.Approved))
        {
            return Error.Rule("indent.not_cancellable", "Only pending or approved indents with nothing issued can be cancelled.");
        }

        Status = IndentStatus.Cancelled;
        DecidedAt = now;
        DecisionNote = reason;
        return Result.Success();
    }

    public Result RecordIssue(Guid lineId, decimal quantity, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("indent.closed", $"Indent {Number} is {Status}.");
        }

        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("IndentLine", lineId);
        }

        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (quantity > line.PendingQuantity)
        {
            return Error.Rule("indent.over_issue", $"{line.ItemName}: only {StockErrors.Format(line.PendingQuantity)} is pending on this indent.");
        }

        line.IssuedQuantity += quantity;
        LastIssuedAt = now;
        Status = Lines.All(l => l.PendingQuantity <= 0) ? IndentStatus.Issued : IndentStatus.PartiallyIssued;
        return Result.Success();
    }

    /// <summary>Short-closes a partially issued indent (the balance will not be supplied).</summary>
    public Result Close(string reason, DateTimeOffset now)
    {
        if (Status != IndentStatus.PartiallyIssued)
        {
            return Error.Rule("indent.not_partial", "Only partially issued indents can be short-closed.");
        }

        Status = IndentStatus.Closed;
        DecidedAt = now;
        DecisionNote = reason;
        return Result.Success();
    }
}

public sealed class IndentLine : Entity
{
    public Guid IndentId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal RequestedQuantity { get; set; }
    public decimal? ApprovedQuantity { get; internal set; }
    public decimal IssuedQuantity { get; internal set; }
    public string? Remarks { get; set; }

    public decimal PendingQuantity => Math.Max((ApprovedQuantity ?? RequestedQuantity) - IssuedQuantity, 0);
}

/// <summary>Issue slip for an indent: exactly which batches left the store (printed and signed on receipt).</summary>
public sealed class IndentIssue : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid IndentId { get; set; }
    public Guid FromStoreId { get; set; }
    public Guid? ToStoreId { get; set; }
    public Guid? DepartmentId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? IssuedByUserId { get; set; }
    public string? ReceivedByName { get; set; }
    public string? Remarks { get; set; }
    public decimal TotalValue { get; set; }
    public List<IndentIssueLine> Lines { get; set; } = [];
}

public sealed class IndentIssueLine : Entity
{
    public Guid IndentIssueId { get; set; }
    public Guid IndentLineId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Value { get; set; }
}
