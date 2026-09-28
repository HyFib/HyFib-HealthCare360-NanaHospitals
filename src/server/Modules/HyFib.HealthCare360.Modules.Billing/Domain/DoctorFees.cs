namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum ShareMethod
{
    /// <summary>Percentage of the line's billed value (after discounts, before GST).</summary>
    Percentage = 0,

    /// <summary>Fixed rupees per unit, capped at the line's billed value.</summary>
    FixedPerUnit,
}

/// <summary>
/// Revenue-sharing rule (DFA-01) for a doctor — or for every doctor when <see cref="PractitionerId"/> is empty —
/// narrowed by service item or category and payer type. The most specific rule in force on the service date wins.
/// </summary>
public sealed class DoctorShareRule : AuditableEntity
{
    public Guid? PractitionerId { get; set; }
    public Guid? ServiceItemId { get; set; }
    public ServiceCategory? Category { get; set; }
    public PayerType? PayerType { get; set; }
    public ShareMethod Method { get; set; }
    public decimal Value { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    public ShareRuleView ToView() => new(Id, PractitionerId, ServiceItemId, Category, PayerType, Method, Value, EffectiveFrom, EffectiveTo);
}

public enum AccrualStatus
{
    Accrued = 0,
    InPayout,
    Paid,

    /// <summary>Cancelled together with its bill before being paid out.</summary>
    Reversed,
}

/// <summary>
/// The doctor's share of one bill line (DFA-02), accrued when the bill is finalized (or paid, per setting). A cancelled
/// bill adds a negative accrual when the original was already in a payout, so the next payout recovers it.
/// </summary>
public sealed class DoctorFeeAccrual : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PractitionerId { get; set; }
    public Guid BillId { get; set; }
    public Guid BillLineId { get; set; }
    public string BillNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid ServiceItemId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ServiceCategory Category { get; set; }
    public PayerType PayerType { get; set; }
    public DateOnly ServiceDate { get; set; }

    /// <summary>Hospital-local date the fee was earned; payout periods and the Tally journal use it.</summary>
    public DateOnly AccruedOn { get; set; }

    public decimal BasisAmount { get; set; }
    public Guid? RuleId { get; set; }
    public ShareMethod Method { get; set; }
    public decimal Rate { get; set; }
    public decimal ShareAmount { get; set; }
    public AccrualStatus Status { get; set; }
    public Guid? PayoutId { get; set; }

    /// <summary>Set on a negative accrual that reverses an earlier one.</summary>
    public Guid? ReversalOfId { get; set; }
}

public enum PayoutStatus
{
    Draft = 0,
    Approved,
    Paid,
}

/// <summary>
/// A doctor's payout for a period (DFA-03..05): accrued shares, deductions (recoveries/advances), TDS under section 194J
/// and the net paid. Creating the payout closes the period for those accruals; a second person approves it.
/// </summary>
public sealed class DoctorPayout : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string PayoutNumber { get; set; } = string.Empty;
    public Guid PractitionerId { get; set; }
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal DeductionAmount { get; set; }
    public decimal TdsPercent { get; set; }
    public decimal TdsAmount { get; set; }
    public decimal NetAmount { get; set; }
    public PayoutStatus Status { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaidReference { get; set; }
    public PaymentMode? PaymentMode { get; set; }
    public string? Notes { get; set; }
    public List<DoctorPayoutDeduction> Deductions { get; set; } = [];

    public void ApplyAmounts(PayoutAmounts amounts)
    {
        GrossAmount = amounts.Gross;
        DeductionAmount = amounts.Deductions;
        TdsPercent = amounts.TdsPercent;
        TdsAmount = amounts.Tds;
        NetAmount = amounts.Net;
    }

    public Result Approve(Guid approverId, DateTimeOffset now)
    {
        if (Status != PayoutStatus.Draft)
        {
            return Error.Rule("doctorfees.payout_not_draft", "Only draft payouts can be approved.");
        }

        if (CreatedBy == approverId)
        {
            return Error.Forbidden("doctorfees.payout_self_approval", "A payout must be approved by someone other than the person who prepared it.");
        }

        Status = PayoutStatus.Approved;
        ApprovedBy = approverId;
        ApprovedAt = now;
        return Result.Success();
    }

    public Result MarkPaid(PaymentMode mode, string? reference, DateTimeOffset now)
    {
        if (Status != PayoutStatus.Approved)
        {
            return Error.Rule("doctorfees.payout_not_approved", "Approve the payout before paying it.");
        }

        if (mode is Domain.PaymentMode.Advance or Domain.PaymentMode.Insurance)
        {
            return Error.Field("mode", "Choose how the doctor is paid (bank transfer, cheque, UPI or cash).");
        }

        if (mode != Domain.PaymentMode.Cash && string.IsNullOrWhiteSpace(reference))
        {
            return Error.Field("reference", "Enter the bank transfer or cheque reference.");
        }

        Status = PayoutStatus.Paid;
        PaymentMode = mode;
        PaidReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        PaidAt = now;
        return Result.Success();
    }
}

public sealed class DoctorPayoutDeduction : Entity
{
    public Guid PayoutId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
