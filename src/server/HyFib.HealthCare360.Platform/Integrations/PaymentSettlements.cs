namespace HyFib.HealthCare360.Platform.Integrations;

// Settlement and refund data for reconciling the payment gateway (INT-PG "refunds and reconciliation"). Billing reads
// them through IPaymentGateway; the Integrations project fills them from the provider (Razorpay's settlement
// reconciliation API) or the simulator. Only identifiers, amounts, the UTR and the payment method travel here: card and
// bank details the provider also returns are never copied.

public enum PaymentSettlementStatus
{
    /// <summary>The gateway has created the settlement but not yet paid it to the bank.</summary>
    Created = 0,

    /// <summary>Paid to the hospital's bank account (the UTR identifies the bank credit).</summary>
    Processed,

    /// <summary>The bank refused the transfer; the gateway settles the money again later.</summary>
    Failed,
}

public enum PaymentTransactionType
{
    Payment = 0,
    Refund,

    /// <summary>A correction by the gateway, such as a chargeback or a fee reversal.</summary>
    Adjustment,

    /// <summary>Money routed to a linked account (not used by HC360 today).</summary>
    Transfer,
    Other,
}

/// <summary>
/// One transaction inside a settlement. <paramref name="Amount"/> is the transaction's own amount; <paramref name="Fee"/>
/// is the gateway's charge including GST, of which <paramref name="Tax"/> is the GST. <paramref name="Credit"/> and
/// <paramref name="Debit"/> are what the transaction added to or took from the settlement (a payment credits its amount
/// less the fee; a refund debits its amount).
/// </summary>
/// <param name="EntityId">The gateway's id of the transaction: a payment (<c>pay_…</c>), refund (<c>rfnd_…</c>) or adjustment.</param>
/// <param name="PaymentId">For a refund, the payment it returned money from.</param>
/// <param name="CreatedAt">When the transaction happened at the gateway (a payment's capture, a refund's creation).</param>
public sealed record PaymentSettlementItem(
    string EntityId,
    PaymentTransactionType Type,
    string? PaymentId,
    string? OrderId,
    decimal Amount,
    decimal Fee,
    decimal Tax,
    decimal Credit,
    decimal Debit,
    string? Method,
    DateTimeOffset? CreatedAt);

/// <summary>
/// A payout from the gateway to the hospital's bank. <paramref name="Amount"/> is what reached the bank;
/// <paramref name="Fees"/> and <paramref name="Tax"/> are the settlement-level figures the gateway reports (zero for
/// standard settlements, whose charges are on the items).
/// </summary>
public sealed record PaymentSettlement(
    string SettlementId,
    string? Utr,
    PaymentSettlementStatus Status,
    decimal Amount,
    decimal Fees,
    decimal Tax,
    DateTimeOffset SettledAt,
    IReadOnlyList<PaymentSettlementItem> Items);

/// <summary>
/// The settlements of one day, or why they could not be read (gateway not configured, unreachable, rejected the
/// credentials). <paramref name="Provider"/> names the gateway that answered, so settlement ids are unique per provider.
/// </summary>
public sealed record PaymentSettlementReport(string Provider, bool Available, IReadOnlyList<PaymentSettlement> Settlements, string? Problem)
{
    public static PaymentSettlementReport Unavailable(string provider, string problem) => new(provider, false, [], problem);
}

public enum PaymentRefundStatus
{
    /// <summary>Accepted by the gateway; the bank has not confirmed it yet (normal refunds take 5 to 7 working days).</summary>
    Pending = 0,

    /// <summary>The money is back with the patient's bank.</summary>
    Processed,

    /// <summary>The bank or the gateway could not return the money; it is still with the hospital.</summary>
    Failed,
}

/// <summary>
/// Where a refund stands at the gateway. When the gateway cannot be asked, the status is <c>Pending</c> and
/// <paramref name="Error"/> says why, so callers keep waiting rather than act on a guess.
/// </summary>
/// <param name="Reference">The bank's reference for the refund (ARN, RRN or UTR), which the patient's bank can trace.</param>
public sealed record RefundStatusResult(
    string RefundId,
    PaymentRefundStatus Status,
    decimal? Amount,
    string? PaymentId,
    string? Reference,
    DateTimeOffset? CreatedAt,
    string? Error);
