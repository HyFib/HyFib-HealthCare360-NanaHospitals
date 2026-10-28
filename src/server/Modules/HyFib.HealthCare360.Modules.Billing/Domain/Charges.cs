namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum ChargeStatus
{
    /// <summary>Captured and not yet on a finalized bill (a draft bill may have reserved it).</summary>
    Unbilled = 0,
    Billed,
    Cancelled,
}

/// <summary>
/// One billable service captured from any module (MB-01): consultation, test, dispense, bed day, procedure.
/// Posting is idempotent on (<see cref="SourceModule"/>, <see cref="SourceReference"/>), so a WiFi retry of the same
/// order line can never charge the patient twice. The price is fixed at capture from the payer's tariff, which keeps
/// the running bill stable when tariffs are revised mid-stay.
/// </summary>
public sealed class Charge : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid ServiceItemId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ServiceCategory Category { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal GrossAmount { get; set; }

    /// <summary>Tariff concession (the plan's default discount). It is policy-driven, so it needs no approval.</summary>
    public decimal DiscountPercent { get; set; }

    public decimal DiscountAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal NetAmount { get; set; }
    public bool IsDiscountAllowed { get; set; } = true;
    public PriceSource PriceSource { get; set; }
    public Guid? TariffPlanId { get; set; }
    public decimal BedMultiplier { get; set; } = 1m;

    /// <summary>For a day of a bed, the kind of room: its GST follows <see cref="RoomRentGst"/>, not the service's own rate.</summary>
    public RoomCharge? Room { get; set; }

    public string SourceModule { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;

    /// <summary>Ordering/performing doctor: drives doctor fee accounting.</summary>
    public Guid? PractitionerId { get; set; }

    public Guid? DepartmentId { get; set; }
    public DateTimeOffset ServiceDate { get; set; }
    public ChargeStatus Status { get; set; }

    /// <summary>The bill that reserved (draft) or billed (finalized) this charge.</summary>
    public Guid? BillId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public bool IsAvailableForBilling => Status == ChargeStatus.Unbilled && BillId is null;

    /// <summary>A return credited to the patient (negative quantity), for example unused ward stock sent back to pharmacy.</summary>
    public bool IsCredit => Quantity < 0;

    /// <summary>
    /// Applies a price quote and recomputes gross, tariff discount, GST (CGST/SGST) and net. A day of a room takes its GST
    /// rate from <see cref="RoomRentGst"/>, by its price after the tariff concession, instead of <paramref name="taxRate"/>.
    /// </summary>
    public void ApplyPrice(PriceQuote quote, decimal taxRate, decimal discountPercent, Guid? tariffPlanId)
    {
        UnitPrice = quote.UnitPrice;
        PriceSource = quote.Source;
        BedMultiplier = quote.Multiplier;
        TariffPlanId = tariffPlanId;
        DiscountPercent = IsDiscountAllowed ? Math.Clamp(discountPercent, 0m, 100m) : 0m;
        TaxRate = Room is { } room
            ? RoomRentGst.RateFor(room, UnitPrice - Money.PercentOf(UnitPrice, DiscountPercent))
            : Math.Max(taxRate, 0m);

        var amounts = ChargeAmounts.Calculate(Quantity, UnitPrice, DiscountPercent, TaxRate);
        GrossAmount = amounts.Gross;
        DiscountAmount = amounts.Discount;
        TaxAmount = amounts.Tax;
        CgstAmount = amounts.Cgst;
        SgstAmount = amounts.Sgst;
        NetAmount = amounts.Net;
    }

    /// <summary>A draft bill claims the charge so two bills can never contain the same service.</summary>
    public Result ReserveFor(Guid billId)
    {
        if (!IsAvailableForBilling)
        {
            return Error.Rule("billing.charge_unavailable", "This service is already on another bill or was cancelled.");
        }

        BillId = billId;
        return Result.Success();
    }

    /// <summary>Returns the charge to the unbilled pool (draft line removed, draft discarded or bill cancelled).</summary>
    public void Release()
    {
        BillId = null;
        if (Status == ChargeStatus.Billed)
        {
            Status = ChargeStatus.Unbilled;
        }
    }

    public void MarkBilled()
    {
        if (Status == ChargeStatus.Unbilled)
        {
            Status = ChargeStatus.Billed;
        }
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status == ChargeStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status == ChargeStatus.Billed)
        {
            return Error.Rule("billing.charge_billed", "This service is on a finalized bill. Cancel the bill or issue a credit note instead.");
        }

        if (BillId is not null)
        {
            return Error.Rule("billing.charge_on_draft", "Remove this service from its draft bill first.");
        }

        Status = ChargeStatus.Cancelled;
        CancelReason = reason;
        CancelledAt = now;
        return Result.Success();
    }
}

public sealed record ChargeAmountsResult(decimal Gross, decimal Discount, decimal Taxable, decimal Tax, decimal Cgst, decimal Sgst, decimal Net);

/// <summary>
/// Line arithmetic: round per line, GST on the discounted value, CGST/SGST split (intra-state supply). A negative
/// quantity is a return credited to the patient (for example ward stock returned to the IP pharmacy): it mirrors the
/// sale exactly — same concession, same GST — with every amount negated, so a sale and its full return net to zero.
/// </summary>
public static class ChargeAmounts
{
    public static ChargeAmountsResult Calculate(decimal quantity, decimal unitPrice, decimal discountPercent, decimal taxRate)
    {
        var sign = quantity < 0 ? -1m : 1m;
        var gross = Money.Round(Math.Abs(quantity) * unitPrice);
        var discount = Math.Min(Money.PercentOf(gross, Math.Clamp(discountPercent, 0m, 100m)), gross);
        var taxable = gross - discount;
        var tax = taxRate > 0 ? Money.PercentOf(taxable, taxRate) : 0m;
        var (cgst, sgst) = Money.SplitGst(tax);
        return new ChargeAmountsResult(sign * gross, sign * discount, sign * taxable, sign * tax, sign * cgst, sign * sgst, sign * (taxable + tax));
    }
}
