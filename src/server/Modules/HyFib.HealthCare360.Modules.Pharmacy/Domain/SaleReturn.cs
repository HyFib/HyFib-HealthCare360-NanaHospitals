namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

/// <summary>
/// Sales return against a pharmacy invoice (PH-02). Unopened, unexpired stock goes back into the batch it came from; a
/// damaged or expired return is written off: it comes back into its batch and goes out again as a write-off, so the stock
/// ledger (and, for a controlled drug, the register) shows what came back and what was destroyed. The refund first reduces
/// any balance due; the rest is paid back the way the sale was paid. A return that pays money out, or writes stock off,
/// is made only when a second person approves it (<c>pharmacy.sale_return</c>).
/// </summary>
public sealed class PharmacySaleReturn : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string ReturnNumber { get; set; } = string.Empty;
    public Guid SaleId { get; set; }
    public Guid? PatientId { get; set; }
    public DateTimeOffset ReturnedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal RefundAmount { get; set; }
    public decimal AdjustedAgainstDue { get; set; }

    /// <summary>What was paid back, in every mode (the name is older than refunds by card and UPI).</summary>
    public decimal CashRefund { get; set; }

    /// <summary>
    /// The mode most of the money went back in. Each part paid back is a refund payment of the sale naming the payment it
    /// pays back (<see cref="PharmacyPayment.RefundOfPaymentId"/>); a sale paid in more than one mode is paid back in each.
    /// </summary>
    public PaymentMode RefundMode { get; set; }

    /// <summary>The reference of the payment most of the money went back to.</summary>
    public string? RefundReference { get; set; }
    public List<PharmacySaleReturnItem> Items { get; set; } = [];
}

public sealed class PharmacySaleReturnItem : Entity
{
    public Guid SaleReturnId { get; set; }
    public Guid SaleItemId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal RefundAmount { get; set; }
    public bool Restocked { get; set; }
}

/// <summary>A line asked to come back: the sale line (or the ward issue line), how much, and whether it goes back on the shelf.</summary>
public sealed record ReturnInput(Guid LineId, decimal Quantity, bool Restock);

/// <summary>A line of a return as worked out: the refund due for it, and whether it is restocked or written off (expired, or not restocked).</summary>
public sealed record SaleReturnPlanLine(PharmacySaleItem Item, decimal Quantity, decimal Refund, bool Restock, bool Expired);

/// <summary>
/// A return worked out before anything changes (<see cref="PharmacySale.PlanReturn"/>): the refund, what is set against the
/// balance due, and how the rest goes back. It waits for a second person when it pays money out or writes stock off.
/// </summary>
public sealed record SaleReturnPlan(IReadOnlyList<SaleReturnPlanLine> Lines, decimal Refund, decimal AgainstDue, IReadOnlyList<RefundPart> PaidBack)
{
    public decimal PaidBackAmount => PaidBack.Sum(p => p.Amount);

    /// <summary>Some of it is not put back on the shelf: a write-off.</summary>
    public bool WritesOff => Lines.Any(l => !l.Restock);

    /// <summary>Money goes out, or stock is written off: a second person approves it first.</summary>
    public bool NeedsApproval => PaidBackAmount > 0 || WritesOff;
}
