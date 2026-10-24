namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum BillType
{
    OutPatient = 0,
    InPatient,
}

public enum BillStatus
{
    Draft = 0,
    Finalized,
    PartiallyPaid,
    Paid,
    Cancelled,
    Refunded,
}

/// <summary>Payer terms captured on a bill so its split can be recalculated without further look-ups.</summary>
public sealed record BillPayerTerms(
    PayerType PayerType,
    Guid? PayerId,
    Guid? TariffPlanId,
    Guid? PolicyId,
    Guid? PreAuthorizationId,
    decimal CoPayPercent,
    decimal? CoverageLimit)
{
    public static BillPayerTerms SelfPay(PayerType payerType, Guid? tariffPlanId) => new(payerType, null, tariffPlanId, null, null, 0m, null);

    /// <summary>True when a third party (insurer, TPA, corporate or government scheme) pays part of the bill.</summary>
    public bool HasPayer => PayerId is not null && PayerType is PayerType.Insurance or PayerType.Corporate or PayerType.GovernmentScheme;
}

/// <summary>
/// OP or IP bill (OPB, IPB, MB-05/06). A draft reserves charges and can change freely; finalization allocates a gap-free
/// number and freezes the lines. The patient share and the payer share are tracked separately: <see cref="DueAmount"/>
/// is what the counter collects, while the payer share is recovered through insurance/corporate claims.
/// </summary>
public sealed class Bill : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>Allocated at finalization, inside the finalizing transaction, so numbers are gap-free.</summary>
    public string? BillNumber { get; set; }

    public BillType Type { get; set; }

    /// <summary>IP discharge bill; other IP bills are interim bills.</summary>
    public bool IsFinalBill { get; set; }

    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? IpAccountId { get; set; }
    public PayerType PayerType { get; set; }
    public Guid? PayerId { get; set; }
    public Guid? TariffPlanId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? PreAuthorizationId { get; set; }

    /// <summary>
    /// OP bills: the package the visit is billed at (MB-02, OPB). Its line is on the bill, and what it absorbs is each
    /// line's <see cref="BillLine.PackageCoveredAmount"/>. An admission's package is on its IP account instead.
    /// </summary>
    public Guid? PackageId { get; set; }

    public decimal CoPayPercent { get; set; }

    /// <summary>Most the payer covers (pre-auth approval); <c>null</c> = no cap (corporate or scheme credit).</summary>
    public decimal? PayerCoverageLimit { get; set; }

    public decimal? BillDiscountPercent { get; set; }
    public decimal? BillDiscountValue { get; set; }
    public string? DiscountReason { get; set; }
    public Guid? DiscountApprovedBy { get; set; }

    /// <summary>User discount (line + bill level) sanctioned by an approver; finalization re-checks against it.</summary>
    public decimal ApprovedDiscountAmount { get; set; }

    public decimal GrossAmount { get; set; }
    public decimal PackageCoveredAmount { get; set; }
    public decimal TariffDiscountAmount { get; set; }
    public decimal LineDiscountAmount { get; set; }
    public decimal BillDiscountAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal RoundOffAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal PayerAmount { get; set; }
    public decimal PatientAmount { get; set; }
    public decimal CoPayAmount { get; set; }
    public decimal NonPayableAmount { get; set; }

    /// <summary>Patient payments (cash, card, UPI, gateway, advance adjustments).</summary>
    public decimal PaidAmount { get; set; }

    public decimal RefundedAmount { get; set; }

    /// <summary>Credit notes and write-offs that reduce what the patient owes.</summary>
    public decimal CreditAmount { get; set; }

    public decimal DueAmount { get; set; }
    public decimal RefundableAmount { get; set; }
    public decimal PayerSettledAmount { get; set; }
    public decimal PayerDisallowedAmount { get; set; }
    public BillStatus Status { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public Guid? FinalizedBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public string? Notes { get; set; }
    public List<BillLine> Lines { get; set; } = [];

    public decimal PayerOutstanding => Math.Max(PayerAmount - PayerSettledAmount - PayerDisallowedAmount, 0m);

    public bool IsOpenForPayment => (Status is BillStatus.Finalized or BillStatus.PartiallyPaid) && DueAmount > 0;

    public bool HasPayer => GetPayerTerms().HasPayer;

    public BillPayerTerms GetPayerTerms() => new(PayerType, PayerId, TariffPlanId, PolicyId, PreAuthorizationId, CoPayPercent, PayerCoverageLimit);

    public static Bill Draft(
        Guid facilityId, BillType type, bool isFinalBill, Guid patientId, Guid? visitId, Guid? admissionId, Guid? ipAccountId, BillPayerTerms terms)
    {
        var bill = new Bill
        {
            FacilityId = facilityId,
            Type = type,
            IsFinalBill = isFinalBill,
            PatientId = patientId,
            VisitId = visitId,
            AdmissionId = admissionId,
            IpAccountId = ipAccountId,
            Status = BillStatus.Draft,
        };
        bill.ApplyPayerTerms(terms);
        return bill;
    }

    public Result ApplyPayerTerms(BillPayerTerms terms)
    {
        if (Status != BillStatus.Draft)
        {
            return NotDraft();
        }

        PayerType = terms.PayerType;
        PayerId = terms.PayerId;
        TariffPlanId = terms.TariffPlanId;
        PolicyId = terms.PolicyId;
        PreAuthorizationId = terms.PreAuthorizationId;
        CoPayPercent = Math.Clamp(terms.CoPayPercent, 0m, 100m);
        PayerCoverageLimit = terms.CoverageLimit is { } limit ? Math.Max(Money.Round(limit), 0m) : null;
        return Result.Success();
    }

    public Result AddCharge(Charge charge, bool isPayerPayable, string? sacCode)
    {
        if (Status != BillStatus.Draft)
        {
            return NotDraft();
        }

        if (charge.PatientId != PatientId)
        {
            return Error.Rule("billing.charge_other_patient", "This service belongs to another patient.");
        }

        var reserved = charge.ReserveFor(Id);
        if (reserved.IsFailure)
        {
            return reserved;
        }

        var order = Lines.Count == 0 ? 1 : Lines.Max(l => l.SortOrder) + 1;
        Lines.Add(BillLine.From(Id, charge, isPayerPayable, sacCode, order));
        return Result.Success();
    }

    /// <summary>Removes a draft line and returns the id of the charge it carried (the caller releases the charge).</summary>
    public Result<Guid> RemoveLine(Guid lineId)
    {
        if (Status != BillStatus.Draft)
        {
            return NotDraft();
        }

        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound(nameof(BillLine), lineId);
        }

        Lines.Remove(line);
        return line.ChargeId;
    }

    /// <summary>Replaces all staff discounts on the draft (line amounts in rupees plus one bill-level discount).</summary>
    public Result SetUserDiscounts(decimal? billPercent, decimal? billAmount, IReadOnlyDictionary<Guid, decimal> lineAmounts, string? reason)
    {
        if (Status != BillStatus.Draft)
        {
            return NotDraft();
        }

        foreach (var line in Lines)
        {
            line.LineDiscountAmount = line.IsDiscountAllowed && line.GrossAmount > 0 && lineAmounts.TryGetValue(line.Id, out var amount)
                ? Math.Max(Money.Round(amount), 0m)
                : 0m;
        }

        BillDiscountPercent = billPercent is > 0 ? Math.Clamp(billPercent.Value, 0m, 100m) : null;
        BillDiscountValue = BillDiscountPercent is null && billAmount is > 0 ? Money.Round(billAmount.Value) : null;
        DiscountReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return Result.Success();
    }

    public void RecordDiscountApproval(Guid approverId, decimal approvedAmount)
    {
        DiscountApprovedBy = approverId;
        ApprovedDiscountAmount = Math.Max(approvedAmount, 0m);
    }

    /// <summary>
    /// Bills an OP draft at a package, or no longer (<c>null</c>). The caller adds or removes the package's line and
    /// applies its cover.
    /// </summary>
    public Result UsePackage(Guid? packageId)
    {
        if (Status != BillStatus.Draft)
        {
            return NotDraft();
        }

        if (Type != BillType.OutPatient)
        {
            return Error.Rule("billing.use_ip_package", "An admission's package is chosen on its IP account.");
        }

        PackageId = packageId;
        return Result.Success();
    }

    /// <summary>
    /// Marks how much of each line the package absorbs (lines not listed are billed in full): the IP account's package
    /// on a final bill, the bill's own on an OP bill. For a credit line the cover is negative: the part of the return
    /// that goes back to the package rather than to the patient.
    /// </summary>
    public void ApplyPackageCoverage(IReadOnlyDictionary<Guid, decimal> coveredByLine)
    {
        foreach (var line in Lines)
        {
            line.PackageCoveredAmount = coveredByLine.TryGetValue(line.Id, out var covered)
                ? Math.Clamp(Money.Round(covered), Math.Min(line.GrossAmount, 0m), Math.Max(line.GrossAmount, 0m))
                : 0m;
        }
    }

    public BillTotals Recalculate()
    {
        var ordered = Lines.OrderBy(l => l.SortOrder).ToList();
        var totals = BillTotalsCalculator.Calculate(
            ordered.Select(l => l.ToInput()).ToList(),
            new BillDiscountInput(BillDiscountPercent, BillDiscountValue),
            new SplitTerms(HasPayer, CoPayPercent, PayerCoverageLimit));

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Apply(totals.Lines[i]);
        }

        GrossAmount = totals.Gross;
        PackageCoveredAmount = totals.PackageCovered;
        TariffDiscountAmount = totals.TariffDiscount;
        LineDiscountAmount = totals.LineDiscount;
        BillDiscountAmount = totals.BillDiscount;
        DiscountAmount = totals.TotalDiscount;
        TaxableAmount = totals.Taxable;
        CgstAmount = totals.Cgst;
        SgstAmount = totals.Sgst;
        TaxAmount = totals.Tax;
        RoundOffAmount = totals.RoundOff;
        NetAmount = totals.Net;
        PayerAmount = totals.PayerAmount;
        PatientAmount = totals.PatientAmount;
        CoPayAmount = totals.CoPay;
        NonPayableAmount = totals.NonPayable;
        UpdateBalances();
        return totals;
    }

    /// <summary>Freezes the bill under its gap-free number and notifies other modules (BillFinalized).</summary>
    public Result Finalize(string billNumber, Guid? userId, DateTimeOffset now)
    {
        if (Status != BillStatus.Draft)
        {
            return NotDraft();
        }

        if (Lines.Count == 0)
        {
            return Error.Rule("billing.bill_empty", "Add at least one service before finalizing the bill.");
        }

        Recalculate();
        BillNumber = billNumber;
        FinalizedAt = now;
        FinalizedBy = userId;
        Status = BillStatus.Finalized;
        UpdateBalances();
        Raise(new BillFinalized(Id, billNumber, PatientId, VisitId, AdmissionId, NetAmount, now, FacilityId));
        RaiseIfSettled(wasPaid: false);
        return Result.Success();
    }

    /// <summary>
    /// Records a patient payment. Counter payments may not exceed the balance; money that has already been captured
    /// by a POS terminal or the payment gateway is always recorded (<paramref name="allowExcess"/>), and any excess
    /// becomes refundable instead of being lost.
    /// </summary>
    public Result ApplyPayment(decimal amount, bool allowExcess)
    {
        if (amount <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter an amount greater than zero.");
        }

        if (Status == BillStatus.Draft)
        {
            return Error.Rule("billing.bill_not_finalized", "Finalize the bill before collecting payment.");
        }

        if (!allowExcess)
        {
            if (!IsOpenForPayment)
            {
                return Error.Rule("billing.bill_not_payable", "This bill has no balance to collect.");
            }

            if (amount > DueAmount)
            {
                return Error.Rule("billing.overpayment", $"The balance on this bill is ₹{Money.FormatInr(DueAmount)}.");
            }
        }

        var wasPaid = Status == BillStatus.Paid;
        PaidAmount += amount;
        UpdateBalances();
        RaiseIfSettled(wasPaid);
        return Result.Success();
    }

    public Result ApplyCredit(decimal amount)
    {
        if (amount <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter an amount greater than zero.");
        }

        if (Status is not (BillStatus.Finalized or BillStatus.PartiallyPaid or BillStatus.Paid))
        {
            return Error.Rule("billing.bill_not_creditable", "Credit notes and write-offs apply to finalized bills only.");
        }

        var creditable = PatientAmount - CreditAmount;
        if (amount > creditable)
        {
            return Error.Rule("billing.credit_exceeds", $"At most ₹{Money.FormatInr(Math.Max(creditable, 0m))} can be credited on this bill.");
        }

        CreditAmount += amount;
        UpdateBalances();
        return Result.Success();
    }

    public Result ApplyRefund(decimal amount)
    {
        if (amount <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter an amount greater than zero.");
        }

        if (amount > RefundableAmount)
        {
            return Error.Rule("billing.refund_exceeds", $"Only ₹{Money.FormatInr(RefundableAmount)} is refundable on this bill.");
        }

        RefundedAmount += amount;
        UpdateBalances();
        return Result.Success();
    }

    /// <summary>
    /// Cancels a finalized bill; whatever the patient paid becomes refundable (BillCancelled is raised). Someone other
    /// than whoever finalized it cancels it, as with refunds and discounts: one person cannot bill, collect and undo.
    /// </summary>
    public Result Cancel(string reason, Guid? userId, DateTimeOffset now)
    {
        if (Status == BillStatus.Draft)
        {
            return Error.Rule("billing.discard_draft", "Draft bills are discarded, not cancelled.");
        }

        if (Status is BillStatus.Cancelled or BillStatus.Refunded)
        {
            return Error.Rule("billing.bill_cancelled", "This bill is already cancelled.");
        }

        if (userId is not null && userId == FinalizedBy)
        {
            return Error.Rule("billing.cancel_own_bill", "You finalized this bill, so someone else must cancel it.");
        }

        Status = BillStatus.Cancelled;
        CancelReason = reason;
        CancelledAt = now;
        CancelledBy = userId;
        UpdateBalances();
        Raise(new BillCancelled(Id, BillNumber ?? string.Empty, FacilityId, PatientId, NetAmount, reason, now));
        return Result.Success();
    }

    /// <summary>Insurer/TPA/corporate settlement against the payer share (disallowed = deductions written off).</summary>
    public Result ApplyPayerSettlement(decimal settled, decimal disallowed)
    {
        if (settled < 0 || disallowed < 0 || settled + disallowed <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter the settled and disallowed amounts.");
        }

        if (settled + disallowed > PayerOutstanding)
        {
            return Error.Rule("billing.payer_settlement_exceeds", $"The payer owes ₹{Money.FormatInr(PayerOutstanding)} on this bill.");
        }

        PayerSettledAmount += settled;
        PayerDisallowedAmount += disallowed;
        return Result.Success();
    }

    /// <summary>Announces that a payment (or payer cover at finalization) settled the patient share — not a credit note.</summary>
    private void RaiseIfSettled(bool wasPaid)
    {
        if (!wasPaid && Status == BillStatus.Paid)
        {
            Raise(new BillPaid(Id, FacilityId));
        }
    }

    private void UpdateBalances()
    {
        var cancelled = Status is BillStatus.Cancelled or BillStatus.Refunded;
        var owed = cancelled ? 0m : PatientAmount - CreditAmount;
        var netPaid = PaidAmount - RefundedAmount;
        var balance = owed - netPaid;
        DueAmount = Math.Max(balance, 0m);
        RefundableAmount = Math.Max(-balance, 0m);

        Status = Status switch
        {
            BillStatus.Draft => BillStatus.Draft,
            BillStatus.Cancelled or BillStatus.Refunded => PaidAmount > 0 && netPaid <= 0 ? BillStatus.Refunded : BillStatus.Cancelled,
            _ => DueAmount == 0 ? BillStatus.Paid : netPaid > 0 ? BillStatus.PartiallyPaid : BillStatus.Finalized,
        };
    }

    private static Error NotDraft() => Error.Rule("billing.bill_not_draft", "Only draft bills can be changed.");
}

/// <summary>A service on a bill; a copy of the charge plus the discount, package and tax split computed for the bill.</summary>
public sealed class BillLine : Entity
{
    public Guid BillId { get; set; }
    public Guid ChargeId { get; set; }
    public Guid ServiceItemId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ServiceCategory Category { get; set; }

    /// <summary>Services Accounting Code printed on GST invoices.</summary>
    public string? SacCode { get; set; }

    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal GrossAmount { get; set; }

    /// <summary>Part of the line absorbed by the admission's package (package vs actuals).</summary>
    public decimal PackageCoveredAmount { get; set; }

    public decimal TariffDiscountPercent { get; set; }
    public decimal TariffDiscountAmount { get; set; }
    public decimal LineDiscountAmount { get; set; }
    public decimal BillDiscountShare { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal NetAmount { get; set; }
    public bool IsDiscountAllowed { get; set; }

    /// <summary>False for items the payer never pays for (IRDAI non-payables such as consumables).</summary>
    public bool IsPayerPayable { get; set; } = true;

    public Guid? PractitionerId { get; set; }
    public Guid? DepartmentId { get; set; }
    public DateTimeOffset ServiceDate { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Value staff discounts are measured against on this line.</summary>
    public decimal DiscountableAmount => IsDiscountAllowed ? Math.Max(GrossAmount - PackageCoveredAmount - TariffDiscountAmount, 0m) : 0m;

    public static BillLine From(Guid billId, Charge charge, bool isPayerPayable, string? sacCode, int sortOrder) => new()
    {
        BillId = billId,
        ChargeId = charge.Id,
        ServiceItemId = charge.ServiceItemId,
        ServiceCode = charge.ServiceCode,
        Description = charge.Description,
        Category = charge.Category,
        SacCode = sacCode,
        Quantity = charge.Quantity,
        UnitPrice = charge.UnitPrice,
        GrossAmount = charge.GrossAmount,
        TariffDiscountPercent = charge.DiscountPercent,
        TariffDiscountAmount = charge.DiscountAmount,
        TaxRate = charge.TaxRate,
        IsDiscountAllowed = charge.IsDiscountAllowed,
        IsPayerPayable = isPayerPayable,
        PractitionerId = charge.PractitionerId,
        DepartmentId = charge.DepartmentId,
        ServiceDate = charge.ServiceDate,
        SortOrder = sortOrder,
        NetAmount = charge.NetAmount,
    };

    public BillLineInput ToInput() =>
        new(GrossAmount, PackageCoveredAmount, TariffDiscountPercent, LineDiscountAmount, IsDiscountAllowed, TaxRate, IsPayerPayable);

    public void Apply(BillLineResult result)
    {
        TariffDiscountAmount = result.TariffDiscount;
        LineDiscountAmount = result.LineDiscount;
        BillDiscountShare = result.BillDiscountShare;
        TaxableAmount = result.Taxable;
        CgstAmount = result.Cgst;
        SgstAmount = result.Sgst;
        TaxAmount = result.Tax;
        NetAmount = result.Net;
    }
}
