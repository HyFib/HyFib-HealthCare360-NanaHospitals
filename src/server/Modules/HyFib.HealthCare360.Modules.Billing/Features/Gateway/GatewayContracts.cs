using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>Push an amount to a card/UPI terminal for a bill (payment) or an IP account (advance).</summary>
public sealed record StartPosSaleRequest(Guid? BillId, Guid? IpAccountId, decimal Amount, string TerminalId, PaymentMode Mode);

public sealed record PosSaleDto(
    Guid Id,
    string Provider,
    string TerminalId,
    string TransactionId,
    decimal Amount,
    PaymentMode Mode,
    PosTransactionStatus Status,
    string? ApprovalCode,
    string? CardLast4,
    string? Rrn,
    string? Message,
    Guid? ReceiptId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Payment link for a bill (defaults to its balance) or an advance for an admission (amount required).</summary>
public sealed record CreatePaymentLinkRequest(Guid? BillId, Guid? IpAccountId, decimal? Amount, bool SendSms = true, bool SendWhatsApp = false);

public sealed record GatewayOrderDto(
    Guid Id,
    string Provider,
    GatewayOrderKind Kind,
    string OrderId,
    decimal Amount,
    string Currency,
    GatewayOrderStatus Status,
    string? LinkUrl,
    string? CheckoutKey,
    Guid? BillId,
    Guid? IpAccountId,
    Guid? ReceiptId,
    PaymentChannel Channel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

public sealed record WebhookAck(bool Processed);

public sealed class StartPosSaleRequestValidator : AbstractValidator<StartPosSaleRequest>
{
    public StartPosSaleRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.BillId is null) != (x.IpAccountId is null)).WithName("billId").WithMessage("Choose a bill or an IP account.");
        RuleFor(x => x.Amount).Amount();
        RuleFor(x => x.TerminalId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Mode).Must(mode => mode is PaymentMode.Card or PaymentMode.Upi).WithMessage("Terminal sales are card or UPI payments.");
    }
}

public sealed class CreatePaymentLinkRequestValidator : AbstractValidator<CreatePaymentLinkRequest>
{
    public CreatePaymentLinkRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.BillId is null) != (x.IpAccountId is null)).WithName("billId").WithMessage("Choose a bill or an IP account.");
        RuleFor(x => x.Amount).OptionalAmount();
        RuleFor(x => x.Amount).NotNull().When(x => x.IpAccountId is not null).WithMessage("Enter the advance amount to request.");
    }
}
