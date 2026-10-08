using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>An acquirer's settlement report as it was imported.</summary>
public sealed record PosSettlementImportDto(
    Guid Id,
    string Acquirer,
    string FileName,
    long FileSizeBytes,
    int Rows,
    int Added,
    int AlreadyImported,
    int Ignored,
    DateOnly? FirstSettledOn,
    DateOnly? LastSettledOn,
    DateTimeOffset ImportedAt,
    string? ImportedByName);

/// <summary>What an import did, in words for the screen, with the rows it left out and why.</summary>
/// <param name="Columns">The headings HC360 read, as <c>Amount from "Txn Amount"</c>.</param>
/// <param name="Problems">Rows left out and why (the first 20).</param>
/// <param name="Matched">Transactions of the report now matched with receipts or refunds.</param>
/// <param name="ToLookAt">Transactions of the report that do not match and need an accountant.</param>
public sealed record PosSettlementImportResult(
    PosSettlementImportDto Import,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> Problems,
    int Matched,
    int ToLookAt,
    string Message);

/// <summary>A settled card or UPI transaction and what it matched (document numbers only, never a patient's name).</summary>
public sealed record PosSettlementLineDto(
    Guid Id,
    string Acquirer,
    PosSettlementLineType Type,
    PaymentMode Mode,
    string? TerminalId,
    DateTimeOffset? TransactionAt,
    DateOnly? TransactionOn,
    decimal Amount,
    decimal Charges,
    decimal Tax,
    decimal Net,
    string? Rrn,
    string? ApprovalCode,
    string? CardLast4,
    DateOnly SettledOn,
    string? Utr,
    PosSettlementMatch Match,
    PosMatchBasis? MatchBasis,
    decimal? RecordedAmount,
    Guid? ReceiptId,
    string? ReceiptNumber,
    Guid? BillId,
    Guid? RefundId,
    string? RefundNumber,
    bool NeedsAttention,
    DateTimeOffset? ReviewedAt,
    string? ReviewedByName,
    string? ReviewNote,
    uint Version);

/// <summary>One payout: a day's transactions under one UTR, which is one credit on the bank statement and one journal.</summary>
/// <param name="Sales">Payments, before charges.</param>
/// <param name="Refunds">Refunds and chargebacks taken back, as a positive amount.</param>
/// <param name="Net">What reached the bank.</param>
/// <param name="VoucherNumber">The settlement journal's number in the Tally day book.</param>
public sealed record PosPayoutDto(
    DateOnly SettledOn,
    string? Utr,
    string Acquirers,
    int Lines,
    decimal Sales,
    decimal Refunds,
    decimal Charges,
    decimal Tax,
    decimal Net,
    int ToLookAt,
    string VoucherNumber);

public sealed record PosSettlementDayDto(
    DateOnly Date,
    int Lines,
    decimal Sales,
    decimal Refunds,
    decimal Charges,
    decimal Tax,
    decimal Net,
    int ToLookAt,
    IReadOnlyList<PosPayoutDto> Payouts);

/// <summary>A card or UPI receipt the acquirer has not settled after the expected days.</summary>
/// <param name="TerminalId">HC360's terminal, for a payment taken on a connected terminal.</param>
public sealed record UnsettledPosReceiptDto(
    Guid ReceiptId,
    string ReceiptNumber,
    Guid PaymentId,
    Guid? BillId,
    DateTimeOffset ReceivedAt,
    PaymentMode Mode,
    decimal Amount,
    string? Reference,
    string? CardLast4,
    string? TerminalId,
    int DaysWaiting);

public sealed record PosSettlementCountDto(PosSettlementMatch Match, int Count, decimal Amount);

/// <summary>The card and UPI reconciliation at a glance for the signed-in facility.</summary>
/// <param name="ToLookAt">Transactions that need an accountant, by why.</param>
/// <param name="Unsettled">Card and UPI receipts the acquirer has not settled after <paramref name="SettlementDays"/> days.</param>
public sealed record PosSettlementSummaryDto(
    int SettlementDays,
    IReadOnlyList<PosSettlementCountDto> ToLookAt,
    int Unsettled,
    decimal UnsettledAmount,
    DateOnly? LastSettledOn,
    PosSettlementImportDto? LastImport,
    IReadOnlyList<string> OtherTerminals);

public sealed record ReviewPosSettlementLineRequest(string Note, uint Version);

/// <param name="Checked">Transactions that did not match before and were looked at again.</param>
public sealed record PosMatchResult(int Checked, int Matched, int ToLookAt, string Message);

public sealed class ReviewPosSettlementLineRequestValidator : AbstractValidator<ReviewPosSettlementLineRequest>
{
    public ReviewPosSettlementLineRequestValidator()
    {
        RuleFor(x => x.Note).NotEmpty().WithMessage("Say what was found or done about it.")
            .MinimumLength(5).WithMessage("Say what was found or done about it in a few words.")
            .SafeText(500);
    }
}
