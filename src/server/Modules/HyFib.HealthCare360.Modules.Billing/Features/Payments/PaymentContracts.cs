using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;

namespace HyFib.HealthCare360.Modules.Billing.Features.Payments;

/// <summary>One mode of a (split) payment with the evidence the cashier captures for reconciliation.</summary>
public sealed record PaymentLineInput(
    PaymentMode Mode,
    decimal Amount,
    string? Reference,
    string? CardLast4,
    string? CardNetwork,
    string? ChequeNumber,
    string? ChequeBank,
    DateOnly? ChequeDate)
{
    public PaymentLineData ToData() => new(Mode, Amount, Reference, CardLast4, CardNetwork, ChequeNumber, ChequeBank, ChequeDate);
}

public sealed record CollectPaymentRequest(Guid BillId, IReadOnlyList<PaymentLineInput> Payments, string? Notes);

public sealed record CollectDepositRequest(IReadOnlyList<PaymentLineInput> Payments, string? Notes);

public sealed record ReceiptPaymentDto(
    PaymentMode Mode,
    decimal Amount,
    string? Reference,
    string? CardLast4,
    string? CardNetwork,
    string? ChequeNumber,
    string? ChequeBank,
    DateOnly? ChequeDate);

public sealed record ReceiptDto(
    Guid Id,
    string ReceiptNumber,
    ReceiptPurpose Purpose,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? BillId,
    string? BillNumber,
    Guid? IpAccountId,
    decimal Amount,
    ReceiptSource Source,
    string? Counter,
    string? ReceivedByName,
    DateTimeOffset ReceivedAt,
    string? Notes,
    IReadOnlyList<ReceiptPaymentDto> Payments);

public sealed record ReceiptListItem(
    Guid Id,
    string ReceiptNumber,
    ReceiptPurpose Purpose,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? BillId,
    decimal Amount,
    string Modes,
    string? Counter,
    string? ReceivedByName,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Due collection (OPB-06): open bills with a balance and unused advances — in-patient deposits plus patient advances
/// such as online appointment prepayments (<paramref name="AdvanceBalance"/> is their sum).
/// </summary>
public sealed record PatientDues(Guid PatientId, decimal TotalDue, decimal AdvanceBalance, IReadOnlyList<BillListItem> Bills, IReadOnlyList<PatientAdvanceDto> Advances);

/// <summary>A patient advance (online appointment prepayment) and what is left of it.</summary>
public sealed record PatientAdvanceDto(
    Guid Id,
    Guid PatientId,
    string ReceiptNumber,
    Guid? AppointmentId,
    string? AppointmentNumber,
    string? Provider,
    decimal Amount,
    decimal AdjustedAmount,
    decimal RefundedAmount,
    decimal Balance,
    decimal RefundPending,
    DateTimeOffset ReceivedAt);

/// <summary>Adjusts a patient advance against a finalized OP bill (<paramref name="Amount"/> defaults to what both allow).</summary>
public sealed record AdjustAdvanceRequest(Guid AdvanceId, decimal? Amount, uint Version);

/// <summary>Refund of a bill, an in-patient advance or a patient advance — exactly one of the three ids.</summary>
public sealed record RequestRefundRequest(Guid? BillId, Guid? IpAccountId, decimal Amount, string Reason, Guid? AdvanceId = null);

public sealed record RefundDto(
    Guid Id,
    string RefundNumber,
    RefundPurpose Purpose,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? BillId,
    Guid? IpAccountId,
    decimal Amount,
    string Reason,
    RefundStatus Status,
    string? RequestedByName,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionRemarks,
    PaymentMode? Mode,
    string? Reference,
    DateTimeOffset? PaidAt,
    Guid? AdvanceId);

public sealed record ApproveRefundRequest(string? Remarks);

public sealed record RejectRefundRequest(string Reason);

public sealed record PayRefundRequest(PaymentMode Mode, string? Reference);

public sealed class PaymentLineInputValidator : AbstractValidator<PaymentLineInput>
{
    public PaymentLineInputValidator()
    {
        RuleFor(x => x.Mode).IsInEnum()
            .Must(mode => mode is not (PaymentMode.Advance or PaymentMode.Insurance or PaymentMode.PaymentGateway))
            .WithMessage("Advance adjustments, insurance settlements and online payments are not entered at the counter.");
        RuleFor(x => x.Amount).Amount();
        RuleFor(x => x.Reference).MaximumLength(64);
        RuleFor(x => x.CardLast4).Matches("^[0-9]{4}$").When(x => !string.IsNullOrEmpty(x.CardLast4)).WithMessage("Enter the last 4 digits of the card.");
        RuleFor(x => x.CardNetwork).MaximumLength(32);
        RuleFor(x => x.ChequeNumber).MaximumLength(12);
        RuleFor(x => x.ChequeBank).MaximumLength(100);
    }
}

public sealed class CollectPaymentRequestValidator : AbstractValidator<CollectPaymentRequest>
{
    public CollectPaymentRequestValidator()
    {
        RuleFor(x => x.BillId).NotEmpty();
        RuleFor(x => x.Payments).NotEmpty().WithMessage("Add at least one payment.").Must(p => p.Count <= 10).WithMessage("Split a payment into at most 10 modes.");
        RuleForEach(x => x.Payments).SetValidator(new PaymentLineInputValidator());
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class CollectDepositRequestValidator : AbstractValidator<CollectDepositRequest>
{
    public CollectDepositRequestValidator()
    {
        RuleFor(x => x.Payments).NotEmpty().WithMessage("Add at least one payment.").Must(p => p.Count <= 10).WithMessage("Split a payment into at most 10 modes.");
        RuleForEach(x => x.Payments).SetValidator(new PaymentLineInputValidator());
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class RequestRefundRequestValidator : AbstractValidator<RequestRefundRequest>
{
    public RequestRefundRequestValidator()
    {
        RuleFor(x => x).Must(x => new[] { x.BillId, x.IpAccountId, x.AdvanceId }.Count(id => id is not null) == 1)
            .WithName("billId").WithMessage("Refund either a bill, an in-patient advance or a patient advance.");
        RuleFor(x => x.Amount).Amount();
        RuleFor(x => x.Reason).Reason();
    }
}

public sealed class AdjustAdvanceRequestValidator : AbstractValidator<AdjustAdvanceRequest>
{
    public AdjustAdvanceRequestValidator()
    {
        RuleFor(x => x.AdvanceId).NotEmpty();
        RuleFor(x => x.Amount).OptionalAmount().GreaterThan(0m).WithMessage("Enter an amount greater than zero.");
    }
}

public sealed class ApproveRefundRequestValidator : AbstractValidator<ApproveRefundRequest>
{
    public ApproveRefundRequestValidator() => RuleFor(x => x.Remarks).SafeText(300);
}

public sealed class RejectRefundRequestValidator : AbstractValidator<RejectRefundRequest>
{
    public RejectRefundRequestValidator() => RuleFor(x => x.Reason).Reason();
}

public sealed class PayRefundRequestValidator : AbstractValidator<PayRefundRequest>
{
    public PayRefundRequestValidator()
    {
        RuleFor(x => x.Mode).IsInEnum()
            .Must(mode => mode is not (PaymentMode.Advance or PaymentMode.Insurance))
            .WithMessage("Choose how the money is returned.");
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}
