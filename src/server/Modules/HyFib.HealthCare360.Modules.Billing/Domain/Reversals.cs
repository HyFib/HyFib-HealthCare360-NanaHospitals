namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>Why money recorded as received or paid out did not move.</summary>
public enum ReversalReason
{
    /// <summary>The bank returned the patient's cheque unpaid, or the cheque the hospital gave was stopped or never presented.</summary>
    ChequeReturned = 0,

    /// <summary>
    /// The card, UPI, net-banking or gateway payment never reached the hospital's bank, or the transfer, card reversal or
    /// gateway refund to the patient failed.
    /// </summary>
    NotSettled,

    /// <summary>A training simulator recorded it: nothing moved.</summary>
    TrainingSimulator,

    Other,

    /// <summary>
    /// The patient's bank took the money back through a chargeback, and the hospital lost it or accepted it
    /// (<see cref="PaymentDispute"/>).
    /// </summary>
    Chargeback,
}

/// <summary>
/// Money a receipt recorded that never arrived: a cheque the bank returned unpaid, a card, UPI or gateway payment the
/// bank never settled, or one a training simulator made up. Receipts are never edited (<see cref="Receipt"/>): the
/// reversal stands beside the receipt, dated the day it is made, so each day's books stay as they were and the day of the
/// reversal carries it. One person asks for it and another approves it (maker-checker), as with refunds.
/// </summary>
/// <remarks>
/// When the money was an advance (an IP deposit or a patient's prepayment) that already paid bills, those payments are
/// undone first, each as a reversal of its own adjustment receipt that names the reversal of the money
/// (<see cref="CausedById"/>): the bills owe again, and the advance holds nothing it did not receive.
/// </remarks>
public sealed class ReceiptReversal : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid ReceiptId { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public ReceiptPurpose Purpose { get; set; }
    public Guid? BillId { get; set; }
    public Guid? IpAccountId { get; set; }
    public Guid? AdvanceId { get; set; }
    public decimal Amount { get; set; }
    public ReversalReason Reason { get; set; }
    public string Note { get; set; } = string.Empty;

    /// <summary>For an advance adjustment undone because the advance's money did not arrive: the reversal of that money.</summary>
    public Guid? CausedById { get; set; }

    public Guid? RequestedBy { get; set; }
    public string? RequestedByName { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTimeOffset ReversedAt { get; set; }
    public List<ReceiptReversalLine> Lines { get; set; } = [];

    /// <summary>Tells other modules that money recorded from the patient never arrived (not for an adjustment undone with it).</summary>
    public void AnnouncePaymentReversed() =>
        Raise(new PaymentReversed(Id, Number, ReceiptId, FacilityId, PatientId, BillId, Amount, ReversedAt));
}

/// <summary>The part of one payment line of a receipt that a reversal took back; in the trail with its reversal.</summary>
[AuditedWithParent]
public sealed class ReceiptReversalLine : Entity
{
    public Guid ReversalId { get; set; }
    public Guid ReceiptPaymentId { get; set; }
    public PaymentMode Mode { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>
/// A refund recorded as paid out that never reached the patient: a cheque stopped or never presented, a transfer, card
/// reversal or gateway refund that failed, or one a training simulator made up. The refund stays as it was recorded, so
/// the day it was paid keeps its books; this record, dated the day it is made, puts the money back on the bill, the IP
/// deposit or the patient advance, from which a new refund can pay the patient. Asked for by one person and approved by
/// another. A refund is reversed at most once.
/// </summary>
public sealed class RefundReversal : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid RefundId { get; set; }
    public string RefundNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public RefundPurpose Purpose { get; set; }
    public Guid? BillId { get; set; }
    public Guid? IpAccountId { get; set; }
    public Guid? AdvanceId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>How the refund was recorded as paid out.</summary>
    public PaymentMode Mode { get; set; }

    public string? Reference { get; set; }
    public string? GatewayRefundId { get; set; }
    public DateTimeOffset PaidAt { get; set; }
    public ReversalReason Reason { get; set; }
    public string Note { get; set; } = string.Empty;
    public Guid? RequestedBy { get; set; }
    public string? RequestedByName { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTimeOffset ReversedAt { get; set; }

    /// <summary>Tells other modules that a refund recorded as paid out never reached the patient.</summary>
    public void AnnounceRefundReversed() =>
        Raise(new RefundPayoutReversed(Id, Number, RefundId, FacilityId, PatientId, BillId, Amount, ReversedAt));
}

/// <summary>What money recorded as moved may be reversed as never having moved.</summary>
public static class ReversalRules
{
    /// <summary>The note a reversal needs: who found that the money did not move, and how.</summary>
    public const int NoteMinLength = 10;

    public const int NoteMaxLength = 500;

    /// <summary>Why a receipt's money cannot be reversed at all, or null.</summary>
    public static Error? ReceiptRefusal(Receipt receipt) => receipt switch
    {
        { Purpose: ReceiptPurpose.PayerSettlement } => Error.Rule(
            "billing.reversal_payer_settlement", "An insurer's settlement is corrected on its claim, not reversed here."),
        { Source: ReceiptSource.DepositAdjustment } => Error.Rule(
            "billing.reversal_adjustment",
            "This receipt only moved an advance onto the bill. Reverse the receipt of the advance's money instead: the bills it paid owe again with it."),
        _ => null,
    };

    /// <summary>
    /// Why a payment line cannot be reversed, or null. <paramref name="reversed"/> is what reversals already took back from it.
    /// Cash was counted at the counter: cash recorded and not received shows as the shift's shortage.
    /// </summary>
    public static Error? LineRefusal(ReceiptPayment line, decimal reversed) => line.Mode switch
    {
        PaymentMode.Cash => Error.Rule(
            "billing.reversal_cash",
            "Cash is counted when the shift closes: cash recorded and not received is the shift's shortage, not a reversal."),
        PaymentMode.Advance or PaymentMode.Insurance => Error.Rule(
            "billing.reversal_mode", "Advance adjustments and insurance settlements are not reversed here."),
        _ when reversed >= line.Amount => Error.Rule("billing.reversal_done", "This payment was reversed already."),
        _ => null,
    };

    /// <summary>Why a paid refund cannot be recorded as not paid out, or null.</summary>
    public static Error? RefundRefusal(Refund refund) => refund switch
    {
        { PayoutReversedAt: not null } => Error.Rule("billing.refund_reversal_done", "This refund was recorded as not paid out already."),
        { Status: not RefundStatus.Paid } => Error.Rule("billing.refund_not_paid", "Only a refund recorded as paid out can be recorded as not paid out."),
        { Mode: PaymentMode.Advance } => Error.Rule(
            "billing.refund_reversal_advance", "This refund only put an advance back; no money left the hospital."),
        { Mode: PaymentMode.Cash } => Error.Rule(
            "billing.refund_reversal_cash",
            "Cash is counted when the shift closes: cash recorded as paid out and still in the drawer is the shift's excess, not a reversal."),

        // The gateway follows its own refunds: one it reports processed reached the patient's bank, and one still pending
        // may yet. A failed one can be sent again, or recorded as not paid out and paid another way.
        { Mode: PaymentMode.PaymentGateway } when !IsGatewayRefundUndone(refund) => Error.Rule(
            "billing.refund_reversal_gateway",
            refund.GatewayStatus == Platform.Integrations.PaymentRefundStatus.Processed
                ? "The payment gateway reports this refund processed: the patient's bank can trace it with its reference."
                : "The payment gateway has not reported this refund failed. Wait for it, or ask the gateway."),
        _ => null,
    };

    // A failed refund being sent again is not undone until the retry ends.
    private static bool IsGatewayRefundUndone(Refund refund) =>
        (refund.GatewayStatus == Platform.Integrations.PaymentRefundStatus.Failed && refund.GatewayRetryStartedAt is null)
        || Platform.Integrations.TrainingSimulators.IsSimulatorRefund(refund.GatewayRefundId);
}
