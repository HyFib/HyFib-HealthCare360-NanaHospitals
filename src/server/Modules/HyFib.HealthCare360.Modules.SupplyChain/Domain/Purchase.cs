namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum RequisitionStatus
{
    Submitted = 0,
    Approved,
    Rejected,
    Ordered,
    Cancelled,
}

/// <summary>A store's request to buy (INV: requisition → PO → GRN). Quantities are in purchase units.</summary>
public sealed class PurchaseRequisition : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid StoreId { get; set; }
    public DateOnly? RequiredBy { get; set; }
    public string? Remarks { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public RequisitionStatus Status { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? RejectReason { get; private set; }
    public Guid? PurchaseOrderId { get; private set; }
    public List<PurchaseRequisitionLine> Lines { get; set; } = [];

    public Result Approve(Guid? userId, DateTimeOffset now)
    {
        if (Status != RequisitionStatus.Submitted)
        {
            return Error.Rule("requisition.not_pending", "Only submitted requisitions can be approved.");
        }

        if (userId is not null && userId == RequestedByUserId)
        {
            return Error.Rule("requisition.approve_own", "You raised this requisition, so someone else must approve it.");
        }

        Status = RequisitionStatus.Approved;
        DecidedByUserId = userId;
        DecidedAt = now;
        return Result.Success();
    }

    public Result Reject(Guid? userId, string reason, DateTimeOffset now)
    {
        if (Status is not (RequisitionStatus.Submitted or RequisitionStatus.Approved))
        {
            return Error.Rule("requisition.closed", "This requisition is already closed.");
        }

        Status = RequisitionStatus.Rejected;
        DecidedByUserId = userId;
        DecidedAt = now;
        RejectReason = reason;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status is not (RequisitionStatus.Submitted or RequisitionStatus.Approved))
        {
            return Error.Rule("requisition.closed", "This requisition is already closed.");
        }

        Status = RequisitionStatus.Cancelled;
        DecidedAt = now;
        RejectReason = reason;
        return Result.Success();
    }

    public Result MarkOrdered(Guid purchaseOrderId)
    {
        if (Status != RequisitionStatus.Approved)
        {
            return Error.Rule("requisition.not_approved", "Only approved requisitions can be turned into a purchase order.");
        }

        Status = RequisitionStatus.Ordered;
        PurchaseOrderId = purchaseOrderId;
        return Result.Success();
    }
}

public sealed class PurchaseRequisitionLine : Entity
{
    public Guid PurchaseRequisitionId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string? Remarks { get; set; }
}

public enum PurchaseOrderStatus
{
    Draft = 0,
    Submitted,
    Approved,
    Sent,
    PartiallyReceived,
    Closed,
    Cancelled,
}

/// <summary>
/// Purchase order to a vendor. Lifecycle: Draft → Submitted → Approved (by a holder of <c>purchase.approve</c>)
/// → Sent → PartiallyReceived → Closed, or Cancelled before any receipt. Amounts exclude GST per line and GST is
/// split CGST/SGST or IGST from the vendor's and facility's GSTIN state codes.
/// </summary>
public sealed class PurchaseOrder : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid VendorId { get; set; }

    /// <summary>Store the goods are delivered to (and received into by GRN).</summary>
    public Guid StoreId { get; set; }

    public Guid? RequisitionId { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDeliveryDate { get; set; }
    public bool IsInterState { get; set; }
    public int PaymentTermsDays { get; set; }
    public string? Remarks { get; set; }
    public PurchaseOrderStatus Status { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>Why the approver sent the order back to draft.</summary>
    public string? ReviewNote { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Touched by every receipt so concurrent GRNs against one order conflict instead of losing updates.</summary>
    public DateTimeOffset? LastReceiptAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CloseReason { get; private set; }
    public decimal GrossAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }
    public decimal IgstAmount { get; private set; }
    public decimal RoundOff { get; private set; }
    public decimal NetAmount { get; private set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];

    public bool IsReceivable => Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived;

    /// <summary>Prices every line and totals the order; call after any line change.</summary>
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

    public Result Submit(DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            return Error.Rule("po.not_draft", "Only draft purchase orders can be submitted.");
        }

        if (Lines.Count == 0)
        {
            return Error.Rule("po.empty", "Add at least one item before submitting.");
        }

        if (Lines.Any(l => l.Quantity <= 0 || l.Rate < 0))
        {
            return Error.Rule("po.invalid_line", "Every line needs a quantity and a rate.");
        }

        Recalculate();
        Status = PurchaseOrderStatus.Submitted;
        SubmittedAt = now;
        ReviewNote = null;
        return Result.Success();
    }

    public Result Approve(Guid? userId, DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Submitted)
        {
            return Error.Rule("po.not_submitted", "Only submitted purchase orders can be approved.");
        }

        // Maker-checker (R-7): the person who raised the order does not also approve it.
        if (userId is not null && userId == CreatedBy)
        {
            return Error.Rule("po.approve_own", "You raised this purchase order, so someone else must approve it.");
        }

        Status = PurchaseOrderStatus.Approved;
        ApprovedByUserId = userId;
        ApprovedAt = now;
        return Result.Success();
    }

    /// <summary>The approver sends the order back for correction.</summary>
    public Result ReturnToDraft(string note)
    {
        if (Status != PurchaseOrderStatus.Submitted)
        {
            return Error.Rule("po.not_submitted", "Only submitted purchase orders can be returned for correction.");
        }

        Status = PurchaseOrderStatus.Draft;
        ReviewNote = note;
        return Result.Success();
    }

    public Result MarkSent(DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Approved)
        {
            return Error.Rule("po.not_approved", "Only approved purchase orders can be sent to the vendor.");
        }

        Status = PurchaseOrderStatus.Sent;
        SentAt = now;
        return Result.Success();
    }

    public Result RecordReceipt(Guid lineId, decimal quantity, decimal freeQuantity, DateTimeOffset now)
    {
        if (!IsReceivable)
        {
            return Error.Rule("po.not_receivable", $"Purchase order {Number} is {Status} and cannot receive goods.");
        }

        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("PurchaseOrderLine", lineId);
        }

        if (quantity < 0 || freeQuantity < 0 || quantity + freeQuantity == 0)
        {
            return StockErrors.InvalidQuantity;
        }

        if (quantity > line.PendingQuantity)
        {
            return Error.Rule("po.over_receipt", $"{line.ItemName}: only {StockErrors.Format(line.PendingQuantity)} {line.PurchaseUnit} are pending on {Number}.");
        }

        line.ReceivedQuantity += quantity;
        line.ReceivedFreeQuantity += freeQuantity;
        LastReceiptAt = now;
        if (Lines.All(l => l.PendingQuantity <= 0))
        {
            Status = PurchaseOrderStatus.Closed;
            ClosedAt = now;
        }
        else
        {
            Status = PurchaseOrderStatus.PartiallyReceived;
        }

        return Result.Success();
    }

    /// <summary>Short-close: the vendor will not supply the balance.</summary>
    public Result Close(string reason, DateTimeOffset now)
    {
        if (!IsReceivable)
        {
            return Error.Rule("po.not_open", "Only approved, sent or partially received orders can be closed.");
        }

        Status = PurchaseOrderStatus.Closed;
        ClosedAt = now;
        CloseReason = reason;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status is PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Closed or PurchaseOrderStatus.Cancelled)
        {
            return Error.Rule("po.not_cancellable", "Orders with receipts or already closed cannot be cancelled; short-close them instead.");
        }

        Status = PurchaseOrderStatus.Cancelled;
        ClosedAt = now;
        CloseReason = reason;
        return Result.Success();
    }
}

public sealed class PurchaseOrderLine : Entity
{
    public Guid PurchaseOrderId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string PurchaseUnit { get; set; } = "NOS";

    /// <summary>Ordered quantity in purchase units.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Scheme / bonus quantity promised free of cost.</summary>
    public decimal FreeQuantity { get; set; }

    /// <summary>Rate per purchase unit, excluding GST.</summary>
    public decimal Rate { get; set; }

    public decimal DiscountPercent { get; set; }
    public decimal GstRate { get; set; }
    public decimal GrossAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }
    public decimal IgstAmount { get; private set; }
    public decimal LineTotal { get; private set; }
    public decimal ReceivedQuantity { get; internal set; }
    public decimal ReceivedFreeQuantity { get; internal set; }

    public decimal PendingQuantity => Math.Max(Quantity - ReceivedQuantity, 0);

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
}
