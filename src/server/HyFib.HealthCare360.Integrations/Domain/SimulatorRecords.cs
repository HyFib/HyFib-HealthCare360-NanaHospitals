namespace HyFib.HealthCare360.Integrations.Domain;

public enum SimulatorChannel
{
    Sms = 0,
    WhatsApp,
    Email,
    Accounting,
}

/// <summary>
/// What a simulator provider "delivered" instead of a real SMS/WhatsApp/e-mail gateway or Tally, so staff can train
/// and test end to end without credentials. Recipients are stored masked, like notification logs. Append-only and
/// excluded from data-change auditing (the integration log already traces every simulated call).
/// </summary>
[NotAudited]
public sealed class SimulatorMessage : Entity
{
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? FacilityId { get; set; }
    public SimulatorChannel Channel { get; set; }
    public string? Recipient { get; set; }
    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? Reference { get; set; }
    public string ProviderMessageId { get; set; } = string.Empty;
}

public enum SimulatorTransactionKind
{
    PaymentOrder = 0,
    PaymentLink,
    PosSale,

    /// <summary>Money returned on a simulated payment: <c>ExternalId</c> is the refund id, <c>Reference</c> the payment id.</summary>
    PaymentRefund,
}

public enum SimulatorTransactionStatus
{
    Created = 0,
    AwaitingCustomer,
    Captured,
    Approved,
    Cancelled,
    Refunded,
}

/// <summary>
/// A simulated payment-gateway order/link, refund or POS sale. Gateway orders are captured immediately; POS sales are
/// approved a few seconds after they are pushed, so the billing screens' polling and cancel flows can be practised.
/// Payments and refunds are later settled to a pretend bank (<see cref="SettlementId"/>, <see cref="SettledAt"/>).
/// </summary>
[NotAudited]
public sealed class SimulatorTransaction : Entity
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? FacilityId { get; set; }
    public SimulatorTransactionKind Kind { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string? Reference { get; set; }
    public string? TerminalId { get; set; }
    public SimulatorTransactionStatus Status { get; set; }
    public string? PaymentId { get; set; }
    public string? ApprovalCode { get; set; }
    public decimal RefundedAmount { get; set; }
    public string? Description { get; set; }

    /// <summary>The simulated settlement that paid this payment (or took this refund) to the bank.</summary>
    public string? SettlementId { get; set; }

    public DateTimeOffset? SettledAt { get; set; }
}
