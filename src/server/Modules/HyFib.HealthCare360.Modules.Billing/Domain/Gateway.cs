using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum GatewayOrderKind
{
    /// <summary>Hosted checkout started from the patient portal.</summary>
    CheckoutOrder = 0,

    /// <summary>Shareable link sent by SMS/WhatsApp for remote payment.</summary>
    PaymentLink,
}

public enum GatewayOrderStatus
{
    Created = 0,
    Paid,
    Failed,
    Expired,
    Cancelled,
}

public enum PaymentChannel
{
    Staff = 0,
    Portal,
}

/// <summary>
/// A payment-gateway order or payment link (INT-PG). The receipt is recorded once, keyed on the gateway payment id,
/// whether the capture is learnt from the signed webhook, a status poll or the portal's return — whichever comes first.
/// </summary>
public sealed class GatewayOrder : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public GatewayOrderKind Kind { get; set; }
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Our reference sent to the gateway (lets the webhook find the order without trusting other payload fields).</summary>
    public string MerchantReference { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public ReceiptPurpose Purpose { get; set; }
    public Guid? BillId { get; set; }
    public Guid? IpAccountId { get; set; }
    public Guid PatientId { get; set; }
    public string? LinkUrl { get; set; }

    /// <summary>Public checkout key for the portal's hosted checkout (never a secret).</summary>
    public string? CheckoutKey { get; set; }

    public GatewayOrderStatus Status { get; set; }
    public string? GatewayPaymentId { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? Method { get; set; }
    public Guid? ReceiptId { get; set; }
    public PaymentChannel Channel { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? LastError { get; set; }

    public void MarkPaid(string paymentId, decimal amount, string? method, Guid receiptId, DateTimeOffset now)
    {
        Status = GatewayOrderStatus.Paid;
        GatewayPaymentId = paymentId;
        PaidAmount = amount;
        Method = method;
        ReceiptId = receiptId;
        PaidAt = now;
        LastError = null;
    }

    public void MarkFailed(string? error)
    {
        if (Status == GatewayOrderStatus.Created)
        {
            Status = GatewayOrderStatus.Failed;
            LastError = error is { Length: > 500 } ? error[..500] : error;
        }
    }
}

/// <summary>
/// A card/UPI sale pushed to a POS terminal (INT-POS). The counter polls its status; the receipt is recorded exactly
/// once when the terminal approves, keyed on the terminal transaction id.
/// </summary>
public sealed class PosTransaction : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public PaymentMode Mode { get; set; }
    public ReceiptPurpose Purpose { get; set; }
    public Guid? BillId { get; set; }
    public Guid? IpAccountId { get; set; }
    public Guid PatientId { get; set; }
    public PosTransactionStatus Status { get; set; }
    public string? ApprovalCode { get; set; }
    public string? CardLast4 { get; set; }
    public string? Rrn { get; set; }
    public string? Message { get; set; }
    public Guid? ReceiptId { get; set; }
    public Guid? InitiatedBy { get; set; }
    public Guid? ShiftId { get; set; }
    public string? Counter { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsComplete => Status is PosTransactionStatus.Approved or PosTransactionStatus.Declined
        or PosTransactionStatus.Cancelled or PosTransactionStatus.TimedOut;

    /// <summary>Applies a terminal status. An approval is final: later polls can never downgrade it.</summary>
    public void Apply(PosTransactionResult result, DateTimeOffset now)
    {
        if (Status == PosTransactionStatus.Approved)
        {
            return;
        }

        Status = result.Status;
        ApprovalCode = result.ApprovalCode ?? ApprovalCode;
        CardLast4 = result.CardLast4 ?? CardLast4;
        Rrn = result.Rrn ?? Rrn;
        Message = result.Message is { Length: > 300 } message ? message[..300] : result.Message;
        if (IsComplete)
        {
            CompletedAt ??= now;
        }
    }
}
