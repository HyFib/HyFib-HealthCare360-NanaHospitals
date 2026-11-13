namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

public enum CanteenCustomerType
{
    Staff = 0,
    Attendant,
    Visitor,
    Patient,
}

public enum CanteenPaymentMode
{
    Cash = 0,
    Upi,
    Card,

    /// <summary>Staff only: added to the employee's credit ledger and deducted from salary by HR.</summary>
    StaffCredit,
}

public enum CanteenSaleStatus
{
    Completed = 0,
    Cancelled,
}

/// <summary>
/// A canteen POS bill (CAN-02) with GST split into CGST and SGST and rounded to the rupee. Bills are cancelled, never
/// edited, and only on their own business day, so the day's closed sales and the staff credit ledger stay consistent.
/// </summary>
public sealed class CanteenSale : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string BillNumber { get; set; } = string.Empty;
    public DateTimeOffset SoldAt { get; set; }

    /// <summary>IST calendar date of the sale (daily summaries and same-day cancellation).</summary>
    public DateOnly BusinessDate { get; set; }

    public CanteenCustomerType CustomerType { get; set; }
    public string? CustomerName { get; set; }
    public string? EmployeeCode { get; set; }
    public string? EmployeeName { get; set; }
    public Guid? PatientId { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal RoundOff { get; set; }
    public decimal NetAmount { get; set; }
    public CanteenPaymentMode PaymentMode { get; set; }
    public string? PaymentReference { get; set; }

    /// <summary>The terminal sale a card or UPI payment was taken on (Billing's record of it); null for a payment keyed in from a slip.</summary>
    public Guid? TerminalSaleId { get; set; }

    /// <summary>The approval code the terminal reported for the payment.</summary>
    public string? ApprovalCode { get; set; }

    /// <summary>The last four digits of the card the terminal reported; never more of the number.</summary>
    public string? CardLast4 { get; set; }

    public CanteenSaleStatus Status { get; set; }
    public string? CashierName { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Who approved the cancellation (a second person, never the cashier who sold the bill).</summary>
    public Guid? CancelledBy { get; set; }

    public string? CancelledByName { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>Who asked for the cancellation.</summary>
    public Guid? CancelRequestedBy { get; set; }

    public string? CancelRequestedByName { get; set; }

    /// <summary>How the money went back: the way it was paid (cash from the till, the card or UPI under its reference, or the staff credit ledger).</summary>
    public CanteenPaymentMode? RefundMode { get; set; }

    public decimal? RefundAmount { get; set; }

    /// <summary>The cashier who sold the bill (who opened it), whoever that was: never the one who approves its cancellation.</summary>
    public bool SoldBy(Guid? userId) => userId is { } id && CreatedBy == id;
    public List<CanteenSaleLine> Lines { get; set; } = [];

    public void ApplyTotals(BillTotals totals)
    {
        Lines = totals.Lines.Select(l => new CanteenSaleLine
        {
            CanteenSaleId = Id,
            MenuItemId = l.MenuItemId,
            ItemName = l.ItemName,
            HsnSac = l.HsnSac,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            GstRate = l.GstRate,
            PriceIncludesGst = l.PriceIncludesGst,
            TaxableAmount = l.TaxableAmount,
            CgstAmount = l.Cgst,
            SgstAmount = l.Sgst,
            LineTotal = l.LineTotal,
        }).ToList();
        TaxableAmount = totals.TaxableAmount;
        CgstAmount = totals.Cgst;
        SgstAmount = totals.Sgst;
        GrossAmount = totals.GrossAmount;
        RoundOff = totals.RoundOff;
        NetAmount = totals.NetAmount;
    }

    /// <summary>Whether the bill can still be cancelled today: not already cancelled, and sold today (earlier days are closed).</summary>
    public Result CanCancel(DateOnly today)
    {
        if (Status == CanteenSaleStatus.Cancelled)
        {
            return Error.Rule("canteen.sale_cancelled", "This bill is already cancelled.");
        }

        return BusinessDate != today
            ? Error.Rule("canteen.day_closed", "Only today's bills can be cancelled; earlier days are closed.")
            : Result.Success();
    }

    /// <summary>
    /// Cancels the bill once a second person approved it: the money goes back the way it was paid, whole
    /// (<see cref="RefundMode"/>), and both names are kept.
    /// </summary>
    public Result Cancel(string reason, Guid? askedBy, string? askedByName, Guid? approvedBy, string? approvedByName, DateOnly today, DateTimeOffset now)
    {
        var cancellable = CanCancel(today);
        if (cancellable.IsFailure)
        {
            return cancellable;
        }

        Status = CanteenSaleStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = approvedBy;
        CancelledByName = approvedByName;
        CancelRequestedBy = askedBy;
        CancelRequestedByName = askedByName;
        CancelReason = reason.Trim();
        RefundMode = PaymentMode;
        RefundAmount = NetAmount;
        return Result.Success();
    }

    /// <summary>How the money goes back, in words for the approver and the bill.</summary>
    public string RefundWords() => PaymentMode switch
    {
        CanteenPaymentMode.Cash => $"₹{Money.FormatInr(NetAmount)} back in cash from the till",
        CanteenPaymentMode.Card => $"₹{Money.FormatInr(NetAmount)} back to the card{Reference()}, not from the till",
        CanteenPaymentMode.Upi => $"₹{Money.FormatInr(NetAmount)} back by UPI{Reference()}, not from the till",
        _ => $"₹{Money.FormatInr(NetAmount)} taken off {EmployeeName ?? EmployeeCode}'s staff credit",
    };

    private string Reference() => string.IsNullOrWhiteSpace(PaymentReference) ? string.Empty : $" (reference {PaymentReference})";
}

/// <summary>A bill line. Name, price, GST rate and HSN/SAC code are copied from the menu item, so later menu edits never change an issued bill.</summary>
public sealed class CanteenSaleLine : Entity
{
    public Guid CanteenSaleId { get; set; }
    public Guid MenuItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;

    /// <summary>HSN/SAC code on the bill; null only on bills made before codes were recorded.</summary>
    public string? HsnSac { get; set; }

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal GstRate { get; set; }
    public bool PriceIncludesGst { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal LineTotal { get; set; }
}
