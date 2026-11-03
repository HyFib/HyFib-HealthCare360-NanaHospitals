using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>A chargeback case as accountants follow it.</summary>
/// <param name="PaymentReference">The gateway's payment id or the terminal transaction's RRN.</param>
/// <param name="DaysLeft">Days until the answer is due (negative when late); null without a deadline or once answered or closed.</param>
/// <param name="LossWaiting">Accepting the loss was asked for and waits for a second person.</param>
public sealed record PaymentDisputeDto(
    Guid Id,
    string Number,
    PaymentDisputeSource Source,
    PaymentDisputeStatus Status,
    Guid ReceiptId,
    string ReceiptNumber,
    Guid ReceiptPaymentId,
    Guid? BillId,
    PaymentMode Mode,
    string? PaymentReference,
    decimal Amount,
    string? ReasonCode,
    string? Reason,
    DateOnly? RespondBy,
    int? DaysLeft,
    DateTimeOffset OpenedAt,
    string? OpenedByName,
    DateOnly? EvidenceSentOn,
    string? EvidenceNote,
    string? EvidenceByName,
    DateTimeOffset? LossAskedAt,
    string? LossAskedByName,
    bool LossWaiting,
    DateTimeOffset? ClosedAt,
    string? ClosedByName,
    string? Outcome,
    string? ReversalNumber,
    uint Version);

/// <summary>Open cases, those due within two days or late, and what they come to.</summary>
public sealed record PaymentDisputeSummaryDto(int Open, decimal OpenAmount, int DueSoon, int Late);

/// <summary>A receipt's card, UPI or online payment line, to choose the one the bank took back.</summary>
/// <param name="Reference">The gateway's payment id, or the RRN or reference the cashier recorded.</param>
/// <param name="HasOpenCase">A chargeback on this line is open already: another is refused.</param>
public sealed record DisputablePaymentDto(Guid Id, PaymentMode Mode, decimal Amount, string? Reference, bool HasOpenCase);

/// <summary>
/// A chargeback the bank or the gateway gave notice of, before a settlement carried it: the receipt (by its number), its
/// payment line when it has more than one card, UPI or online payment, the amount, and what the notice says.
/// </summary>
public sealed record OpenPaymentDisputeRequest(string ReceiptNumber, Guid? PaymentId, decimal Amount, string? ReasonCode, string? Reason, DateOnly? RespondBy);

/// <summary>What the bank's or the gateway's notice says.</summary>
public sealed record DescribePaymentDisputeRequest(string? ReasonCode, string? Reason, DateOnly? RespondBy, uint Version);

/// <summary>The evidence the hospital sent, and when.</summary>
public sealed record PaymentDisputeEvidenceRequest(DateOnly SentOn, string Note, uint Version);

/// <summary>How the case was won (the money came back, or the bank dropped it), or why the loss is accepted.</summary>
public sealed record PaymentDisputeNoteRequest(string Note, uint Version);

public sealed class OpenPaymentDisputeRequestValidator : AbstractValidator<OpenPaymentDisputeRequest>
{
    public OpenPaymentDisputeRequestValidator()
    {
        RuleFor(x => x.ReceiptNumber).NotEmpty().WithMessage("Enter the receipt's number.").SafeText(24);
        RuleFor(x => x.Amount).GreaterThan(0m).WithMessage("Enter the amount the bank took back.")
            .Must(a => decimal.Round(a, 2) == a).WithMessage("Enter the amount in rupees and paise.");
        RuleFor(x => x.ReasonCode).SafeText(32);
        RuleFor(x => x.Reason).SafeText(PaymentDispute.NoteMaxLength);
    }
}

public sealed class DescribePaymentDisputeRequestValidator : AbstractValidator<DescribePaymentDisputeRequest>
{
    public DescribePaymentDisputeRequestValidator()
    {
        RuleFor(x => x.ReasonCode).SafeText(32);
        RuleFor(x => x.Reason).SafeText(PaymentDispute.NoteMaxLength);
    }
}

public sealed class PaymentDisputeEvidenceRequestValidator : AbstractValidator<PaymentDisputeEvidenceRequest>
{
    public PaymentDisputeEvidenceRequestValidator()
    {
        RuleFor(x => x.Note).NotEmpty().WithMessage("Say what was sent, and to whom.")
            .MinimumLength(5).WithMessage("Say what was sent, and to whom, in a few words.")
            .SafeText(PaymentDispute.NoteMaxLength);
    }
}

public sealed class PaymentDisputeNoteRequestValidator : AbstractValidator<PaymentDisputeNoteRequest>
{
    public PaymentDisputeNoteRequestValidator()
    {
        RuleFor(x => x.Note).NotEmpty().WithMessage("Say what happened.")
            .MinimumLength(5).WithMessage("Say what happened in a few words.")
            .SafeText(ReversalRules.NoteMaxLength);
    }
}
