using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;

namespace HyFib.HealthCare360.Modules.Billing.Features.Portal;

public sealed record PortalBillListItem(
    Guid Id,
    string? BillNumber,
    BillType Type,
    BillStatus Status,
    Guid PatientId,
    string? PatientName,
    DateTimeOffset? BilledAt,
    decimal NetAmount,
    decimal PatientAmount,
    decimal PaidAmount,
    decimal DueAmount);

/// <summary>A bill line as the patient sees it; <paramref name="IsReturn"/> marks goods returned and credited (negative amounts).</summary>
public sealed record PortalBillLine(
    string Description, decimal Quantity, decimal UnitPrice, decimal DiscountAmount, decimal TaxAmount, decimal NetAmount, DateTimeOffset ServiceDate, bool IsReturn);

/// <summary>What a patient sees of a bill: services, amounts and payments — no internal codes or staff names.</summary>
public sealed record PortalBillDetail(
    Guid Id,
    string? BillNumber,
    BillType Type,
    BillStatus Status,
    Guid PatientId,
    string? PatientName,
    DateTimeOffset? BilledAt,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal RoundOffAmount,
    decimal NetAmount,
    decimal PayerAmount,
    decimal PatientAmount,
    decimal PaidAmount,
    decimal DueAmount,
    IReadOnlyList<PortalBillLine> Lines,
    IReadOnlyList<ReceiptSummary> Receipts);

/// <summary>Hosted-checkout order for "pay now" (the checkout key is the gateway's public key, never a secret).</summary>
public sealed record PortalPaymentOrder(Guid Id, string Provider, string OrderId, decimal Amount, string Currency, string? CheckoutKey, string? PaymentLinkUrl);

public sealed record PortalPaymentStatus(GatewayOrderStatus Status, Guid? ReceiptId, decimal DueAmount);
