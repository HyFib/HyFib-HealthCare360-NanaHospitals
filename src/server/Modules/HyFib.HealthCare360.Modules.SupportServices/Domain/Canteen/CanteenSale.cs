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
    public CanteenSaleStatus Status { get; set; }
    public string? CashierName { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
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

    public Result Cancel(string reason, Guid? userId, DateOnly today, DateTimeOffset now)
    {
        if (Status == CanteenSaleStatus.Cancelled)
        {
            return Error.Rule("canteen.sale_cancelled", "This bill is already cancelled.");
        }

        if (BusinessDate != today)
        {
            return Error.Rule("canteen.day_closed", "Only today's bills can be cancelled; earlier days are closed.");
        }

        Status = CanteenSaleStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = userId;
        CancelReason = reason.Trim();
        return Result.Success();
    }
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
