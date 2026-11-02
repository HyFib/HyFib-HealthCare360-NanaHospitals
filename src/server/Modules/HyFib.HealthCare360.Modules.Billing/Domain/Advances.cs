namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// Patient money held before a bill exists — today the online prepayment of an appointment (OA-03). It is receipted
/// when the gateway confirms it, adjusted against the consultation bill when the visit is billed (a journal, no money
/// moves), and refunded if the booking is cancelled. One advance per gateway payment, however often the event is
/// replayed. The balance is tracked here (not by editing receipts), with the row version guarding concurrent use.
/// </summary>
public sealed class PatientAdvance : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }

    /// <summary>The receipt (purpose Advance) that recorded the money.</summary>
    public Guid ReceiptId { get; set; }

    public string ReceiptNumber { get; set; } = string.Empty;
    public Guid? AppointmentId { get; set; }
    public string? AppointmentNumber { get; set; }
    public string? Provider { get; set; }
    public string GatewayOrderId { get; set; } = string.Empty;

    /// <summary>Gateway payment id: the natural key (it follows a rescheduled booking) and the id refunds reverse.</summary>
    public string GatewayPaymentId { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public decimal AdjustedAmount { get; set; }
    public decimal RefundedAmount { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public decimal Balance => Amount - AdjustedAmount - RefundedAmount;

    /// <summary>Uses part of the advance to pay a bill.</summary>
    public Result Adjust(decimal amount) => Consume(amount, adjust: true);

    /// <summary>Returns the money to the patient (the refund is paid out).</summary>
    public Result Refund(decimal amount) => Consume(amount, adjust: false);

    /// <summary>Puts an adjustment back (the bill it paid was cancelled, or the payment is undone).</summary>
    public void ReverseAdjustment(decimal amount) => AdjustedAmount = Math.Max(AdjustedAmount - Math.Max(amount, 0m), 0m);

    /// <summary>
    /// The advance's money never arrived (a receipt reversal): it holds that much less. Only an unused balance can go:
    /// the bills it paid are undone first, and a refund paid from it is recorded as not paid out first.
    /// </summary>
    public Result Reverse(decimal amount)
    {
        if (amount <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter an amount greater than zero.");
        }

        if (amount > Balance)
        {
            return Error.Rule(
                "billing.reversal_exceeds_held",
                $"Advance {ReceiptNumber} holds ₹{Money.FormatInr(Math.Max(Balance, 0m))}: ₹{Money.FormatInr(RefundedAmount)} of it was refunded. " +
                "If a refund was not paid out, record that first.");
        }

        Amount -= amount;
        return Result.Success();
    }

    /// <summary>A refund recorded as paid out of the advance was not paid: the advance holds that money again.</summary>
    public Result ReverseRefund(decimal amount)
    {
        if (amount <= 0 || amount > RefundedAmount)
        {
            return Error.Rule("billing.refund_reversal_exceeds", $"Advance {ReceiptNumber} has ₹{Money.FormatInr(RefundedAmount)} of refunds recorded.");
        }

        RefundedAmount -= amount;
        return Result.Success();
    }

    private Result Consume(decimal amount, bool adjust)
    {
        if (amount <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter an amount greater than zero.");
        }

        if (amount > Balance)
        {
            return Error.Rule("billing.advance_insufficient", $"The advance balance is ₹{Money.FormatInr(Math.Max(Balance, 0m))}.");
        }

        if (adjust)
        {
            AdjustedAmount += amount;
        }
        else
        {
            RefundedAmount += amount;
        }

        return Result.Success();
    }
}
