using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

public enum SaleType
{
    /// <summary>Dispensed against an e-prescription from the queue (or a refill).</summary>
    Prescription = 0,

    /// <summary>Counter sale: OTC, or against an outside (paper) prescription.</summary>
    Counter,
}

/// <summary>
/// Pharmacy GST invoice (PH-01/02, MB-06). The pharmacy runs its own counter billing: items carry the batch,
/// expiry and MRP actually handed over, GST is split CGST/SGST, and payments may be split across cash, card,
/// UPI and credit. Invoices are immutable; corrections are same-day cancellation or a sales return.
/// </summary>
public sealed class PharmacySale : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public SaleType Type { get; set; }
    public Guid StoreId { get; set; }
    public Guid? PatientId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public Guid? QueueEntryId { get; set; }
    public Guid? PrescriptionId { get; set; }
    public string? PrescriptionNumber { get; set; }
    public Guid? PractitionerId { get; set; }
    public string? PrescriberName { get; set; }
    public string? PrescriberRegistrationNumber { get; set; }
    public DateTimeOffset SoldAt { get; set; }
    public Guid? SoldByUserId { get; set; }
    public string? CounsellingNotes { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal GrossAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }

    /// <summary>GST on goods delivered in another state (<see cref="IsInterState"/>).</summary>
    public decimal IgstAmount { get; private set; }

    public decimal RoundOff { get; private set; }
    public decimal NetAmount { get; private set; }
    public decimal PaidAmount { get; private set; }
    public decimal DueAmount { get; private set; }
    public decimal ReturnedAmount { get; private set; }
    public decimal RefundedAmount { get; private set; }
    public SaleStatus Status { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public List<PharmacySaleItem> Items { get; set; } = [];
    public List<PharmacyPayment> Payments { get; set; } = [];

    /// <summary>A registered buyer (B2B): the GSTIN and legal name the tax invoice is made out to.</summary>
    public string? BuyerGstin { get; private set; }

    public string? BuyerLegalName { get; private set; }

    /// <summary>The state of supply as its GST code: where the goods are delivered (the counter's own state when handed over there).</summary>
    public string? PlaceOfSupply { get; private set; }

    public bool IsInterState { get; private set; }

    /// <summary>
    /// The buyer and the place of supply, before the items are priced: goods handed over at the counter are supplied in
    /// the facility's state; goods dispatched to another state are an inter-state supply, taxed as IGST.
    /// </summary>
    public void SetSupply(string? buyerGstin, string? buyerLegalName, string? facilityState, string? deliveryState)
    {
        BuyerGstin = buyerGstin;
        BuyerLegalName = buyerLegalName;
        PlaceOfSupply = deliveryState ?? facilityState;
        IsInterState = deliveryState is not null && facilityState is not null && deliveryState != facilityState;
    }

    /// <summary>Totals the priced items and records the payment split taken at the counter.</summary>
    public Result Complete(IReadOnlyList<SalePayment> payments, bool creditAllowed, DateTimeOffset now, Guid? userId)
    {
        if (Items.Count == 0)
        {
            return Error.Rule("sale.empty", "Nothing was sold.");
        }

        var totals = SalePricing.Total(Items.Select(i => new SaleLinePrice(i.GrossAmount, i.DiscountAmount, i.TaxableAmount, i.CgstAmount, i.SgstAmount, i.NetAmount, i.IgstAmount)));
        var settled = SalePricing.Settle(totals.Net, payments, creditAllowed);
        if (settled.IsFailure)
        {
            return settled.Error!;
        }

        GrossAmount = totals.Gross;
        DiscountAmount = totals.Discount;
        TaxableAmount = totals.Taxable;
        CgstAmount = totals.Cgst;
        SgstAmount = totals.Sgst;
        IgstAmount = totals.Igst;
        RoundOff = totals.RoundOff;
        NetAmount = totals.Net;
        PaidAmount = settled.Value.Paid;
        DueAmount = settled.Value.Due;
        Status = settled.Value.Status;
        foreach (var payment in payments)
        {
            Payments.Add(new PharmacyPayment
            {
                PharmacySaleId = Id,
                Mode = payment.Mode,
                Amount = payment.Amount,
                Reference = payment.Reference?.Trim(),
                ReceivedAt = now,
                ReceivedByUserId = userId,
            });
        }

        return Result.Success();
    }

    /// <summary>Collects an outstanding (credit) balance later.</summary>
    public Result CollectPayment(PaymentMode mode, decimal amount, string? reference, DateTimeOffset now, Guid? userId)
    {
        if (Status == SaleStatus.Cancelled)
        {
            return Error.Rule("sale.cancelled", $"Invoice {InvoiceNumber} is cancelled.");
        }

        if (mode == PaymentMode.Credit)
        {
            return Error.Field("mode", "Collect the balance in cash, card or UPI.");
        }

        if (amount <= 0 || amount > DueAmount)
        {
            return Error.Rule("payment.exceeds_due", $"The balance due on {InvoiceNumber} is Rs.{Money.FormatInr(DueAmount)}.");
        }

        Payments.Add(new PharmacyPayment { PharmacySaleId = Id, Mode = mode, Amount = amount, Reference = reference?.Trim(), ReceivedAt = now, ReceivedByUserId = userId });
        PaidAmount += amount;
        DueAmount -= amount;
        Status = SalePricing.StatusFor(PaidAmount, DueAmount);
        return Result.Success();
    }

    /// <summary>
    /// Books a sales return worth <paramref name="refund"/>: it first reduces any balance still due (a credit note),
    /// the rest is paid back. Returns (adjusted against due, paid back).
    /// </summary>
    public Result<(decimal AdjustedAgainstDue, decimal CashRefund)> ApplyReturn(decimal refund, PaymentMode refundMode, string? reference, DateTimeOffset now, Guid? userId)
    {
        if (Status == SaleStatus.Cancelled)
        {
            return Error.Rule("sale.cancelled", $"Invoice {InvoiceNumber} is cancelled.");
        }

        var adjusted = Math.Min(refund, DueAmount);
        var cash = refund - adjusted;
        DueAmount -= adjusted;
        ReturnedAmount += refund;
        RefundedAmount += cash;
        if (cash > 0)
        {
            Payments.Add(new PharmacyPayment
            {
                PharmacySaleId = Id,
                Mode = refundMode == PaymentMode.Credit ? PaymentMode.Cash : refundMode,
                Amount = cash,
                Reference = reference?.Trim(),
                ReceivedAt = now,
                ReceivedByUserId = userId,
                IsRefund = true,
            });
        }

        Status = SalePricing.StatusFor(PaidAmount, DueAmount);
        return (adjusted, cash);
    }

    /// <summary>Same-day void of an invoice with no returns; the money taken is refunded and stock goes back.</summary>
    public Result Cancel(string reason, DateOnly today, DateOnly soldOn, DateTimeOffset now, Guid? userId)
    {
        if (Status == SaleStatus.Cancelled)
        {
            return Error.Rule("sale.cancelled", $"Invoice {InvoiceNumber} is already cancelled.");
        }

        if (soldOn != today)
        {
            return Error.Rule("sale.cancel_window", "Only today's invoices can be cancelled; use a sales return for older invoices.");
        }

        if (ReturnedAmount > 0 || Items.Any(i => i.ReturnedQuantity > 0))
        {
            return Error.Rule("sale.has_returns", "Invoices with returns cannot be cancelled.");
        }

        if (PaidAmount > 0)
        {
            Payments.Add(new PharmacyPayment { PharmacySaleId = Id, Mode = PaymentMode.Cash, Amount = PaidAmount, ReceivedAt = now, ReceivedByUserId = userId, IsRefund = true, Reference = "Invoice cancelled" });
        }

        RefundedAmount = PaidAmount;
        DueAmount = 0;
        Status = SaleStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        return Result.Success();
    }

    /// <summary>Feeds the medication history / MAR (<see cref="MedicationDispensed"/>) for registered patients.</summary>
    public void MarkDispensed(Guid? admissionId)
    {
        if (PatientId is not { } patientId)
        {
            return;
        }

        Raise(new MedicationDispensed(
            Id, patientId, admissionId, PrescriptionId,
            Items.Select(i => new DispensedLine(i.PrescriptionLineId, i.ItemId, i.ItemName, i.BatchNumber, i.Quantity)).ToList(),
            SoldAt));
    }
}

public sealed class PharmacySaleItem : Entity
{
    public Guid PharmacySaleId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? HsnCode { get; set; }
    public DrugSchedule Schedule { get; set; }
    public bool IsControlled { get; set; }
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>MRP per unit sold (GST inclusive).</summary>
    public decimal UnitMrp { get; set; }

    public decimal GstRate { get; set; }
    public decimal GrossAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }
    public decimal IgstAmount { get; private set; }
    public decimal NetAmount { get; private set; }
    public decimal ReturnedQuantity { get; private set; }
    public decimal RefundedAmount { get; private set; }
    public Guid? QueueLineId { get; set; }
    public Guid? PrescriptionLineId { get; set; }
    public bool IsSubstitution { get; set; }
    public string? PrescribedDrugName { get; set; }
    public string? SubstitutionReason { get; set; }
    public bool PrescriberConsent { get; set; }

    public decimal ReturnableQuantity => Quantity - ReturnedQuantity;

    public void Price(decimal discountPercent, bool interState = false)
    {
        var price = SalePricing.PriceLine(UnitMrp, Quantity, GstRate, discountPercent, interState);
        GrossAmount = price.Gross;
        DiscountAmount = price.Discount;
        TaxableAmount = price.Taxable;
        CgstAmount = price.Cgst;
        SgstAmount = price.Sgst;
        IgstAmount = price.Igst;
        NetAmount = price.Net;
    }

    /// <summary>Returns part of the line and gives the refund due for it.</summary>
    public Result<decimal> Return(decimal quantity)
    {
        if (quantity <= 0 || quantity > ReturnableQuantity)
        {
            return Error.Rule("return.exceeds_sold", $"{ItemName}: at most {StockErrors.Format(ReturnableQuantity)} can be returned.");
        }

        var refund = SalePricing.RefundFor(NetAmount, Quantity, ReturnedQuantity, RefundedAmount, quantity);
        ReturnedQuantity += quantity;
        RefundedAmount += refund;
        return refund;
    }
}

/// <summary>A payment (or refund) against an invoice.</summary>
public sealed class PharmacyPayment : Entity
{
    public Guid PharmacySaleId { get; set; }
    public PaymentMode Mode { get; set; }
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? ReceivedByUserId { get; set; }
    public bool IsRefund { get; set; }
}
