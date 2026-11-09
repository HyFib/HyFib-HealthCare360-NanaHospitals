namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>A debit note or payable adjustment: raised and waiting for a second person, posted, or cancelled.</summary>
public enum VendorDocumentStatus
{
    Draft = 0,
    Posted,
    Cancelled,
}

/// <summary>Whether a payable adjustment lowers or raises what the hospital owes the supplier.</summary>
public enum PayableAdjustmentDirection
{
    /// <summary>The hospital owes less: a rate difference, a discount or scheme credit the supplier gave.</summary>
    Reduce = 0,

    /// <summary>The hospital owes more: a short-billed invoice, freight the supplier charged later.</summary>
    Increase,
}

/// <summary>
/// A purchase return (debit note) to a supplier: stock handed back from a store's batches (a recall, damage, short expiry,
/// wrong supply), valued at what it cost, with the input GST it takes back. Whoever raises it does not post it: a second
/// person who checks goods receipts approves it, and only then does the stock leave the store and the note reach the Tally
/// day book (MAKER-CHECKER slice 13).
/// </summary>
public sealed class VendorDebitNote : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid VendorId { get; set; }
    public Guid StoreId { get; set; }

    /// <summary>The goods receipt the stock came in on, when known.</summary>
    public Guid? GoodsReceiptId { get; set; }

    /// <summary>The supplier's own reference for the return (their credit note or return authorisation number).</summary>
    public string? VendorReference { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsInterState { get; set; }
    public VendorDocumentStatus Status { get; private set; }
    public DateTimeOffset RaisedAt { get; set; }
    public Guid? RaisedByUserId { get; set; }
    public DateTimeOffset? PostedAt { get; private set; }
    public Guid? PostedByUserId { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }
    public decimal IgstAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public List<VendorDebitNoteLine> Lines { get; set; } = [];

    /// <summary>Adds a line valued at <paramref name="unitCost"/> a base unit, with GST split as the supply is within or between states.</summary>
    public void AddLine(StockBatch batch, string itemName, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var taxable = Money.Round(quantity * batch.PurchaseRate);
        var gst = Money.Round(taxable * batch.GstRate / 100m);
        var cgst = IsInterState ? 0m : Money.Round(gst / 2m);
        Lines.Add(new VendorDebitNoteLine
        {
            VendorDebitNoteId = Id,
            StockBatchId = batch.Id,
            ItemId = batch.ItemId,
            ItemName = itemName,
            BatchNumber = batch.BatchNumber,
            ExpiryDate = batch.ExpiryDate,
            Quantity = quantity,
            UnitCost = batch.PurchaseRate,
            GstRate = batch.GstRate,
            TaxableAmount = taxable,
            CgstAmount = cgst,
            SgstAmount = IsInterState ? 0m : gst - cgst,
            IgstAmount = IsInterState ? gst : 0m,
        });
        TaxableAmount = Lines.Sum(l => l.TaxableAmount);
        CgstAmount = Lines.Sum(l => l.CgstAmount);
        SgstAmount = Lines.Sum(l => l.SgstAmount);
        IgstAmount = Lines.Sum(l => l.IgstAmount);
        TotalAmount = TaxableAmount + CgstAmount + SgstAmount + IgstAmount;
    }

    public bool IsRaisedBy(Guid? userId) => userId is { } user && RaisedByUserId == user;

    public Result MarkPosted(Guid? userId, DateTimeOffset now)
    {
        if (Status != VendorDocumentStatus.Draft)
        {
            return Error.Rule("debit_note.not_draft", $"Debit note {Number} is {Status}.");
        }

        Status = VendorDocumentStatus.Posted;
        PostedAt = now;
        PostedByUserId = userId;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status != VendorDocumentStatus.Draft)
        {
            return Error.Rule("debit_note.not_draft", $"Debit note {Number} is {Status} and cannot be cancelled.");
        }

        Status = VendorDocumentStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        return Result.Success();
    }
}

/// <summary>One batch handed back on a debit note; in the audit trail with its note (what was returned, and how much).</summary>
[AuditedWithParent]
public sealed class VendorDebitNoteLine : Entity
{
    public Guid VendorDebitNoteId { get; set; }
    public Guid StockBatchId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal GstRate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal IgstAmount { get; set; }
}

/// <summary>
/// A change to what the hospital owes a supplier that no goods move with: a rate difference, a scheme credit, freight billed
/// later. Whoever raises it does not post it: a second person who approves purchases decides, and only then does it reach
/// the Tally day book as a journal against the supplier (MAKER-CHECKER slice 13).
/// </summary>
public sealed class VendorPayableAdjustment : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid VendorId { get; set; }
    public PayableAdjustmentDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? VendorReference { get; set; }
    public VendorDocumentStatus Status { get; private set; }
    public DateTimeOffset RaisedAt { get; set; }
    public Guid? RaisedByUserId { get; set; }
    public DateTimeOffset? PostedAt { get; private set; }
    public Guid? PostedByUserId { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }

    public Result MarkPosted(Guid? userId, DateTimeOffset now)
    {
        if (Status != VendorDocumentStatus.Draft)
        {
            return Error.Rule("payable_adjustment.not_draft", $"Payable adjustment {Number} is {Status}.");
        }

        Status = VendorDocumentStatus.Posted;
        PostedAt = now;
        PostedByUserId = userId;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status != VendorDocumentStatus.Draft)
        {
            return Error.Rule("payable_adjustment.not_draft", $"Payable adjustment {Number} is {Status} and cannot be cancelled.");
        }

        Status = VendorDocumentStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        return Result.Success();
    }
}
