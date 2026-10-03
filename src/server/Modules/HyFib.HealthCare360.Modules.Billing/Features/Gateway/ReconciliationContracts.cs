using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>A settlement in the list: the bank payout and what made it up.</summary>
/// <param name="Difference">What reached the bank less what its transactions add up to (zero when consistent).</param>
/// <param name="Unmatched">Transactions with no matching receipt or refund, or a different amount.</param>
/// <param name="IsShare">
/// The settlement is held by another facility on the same merchant account: the amounts and counts are this facility's
/// own transactions only.
/// </param>
public sealed record GatewaySettlementDto(
    Guid Id,
    string Provider,
    string SettlementId,
    string? Utr,
    PaymentSettlementStatus Status,
    DateTimeOffset SettledAt,
    DateOnly SettledOn,
    decimal PaymentsAmount,
    decimal RefundsAmount,
    decimal AdjustmentsAmount,
    decimal Charges,
    decimal Tax,
    decimal Amount,
    decimal Difference,
    int Lines,
    int Unmatched,
    bool IsShare);

/// <summary>One day of settlements with its totals.</summary>
public sealed record SettlementDayDto(
    DateOnly Date,
    int Count,
    decimal PaymentsAmount,
    decimal RefundsAmount,
    decimal AdjustmentsAmount,
    decimal Charges,
    decimal Tax,
    decimal Amount,
    int Unmatched,
    IReadOnlyList<GatewaySettlementDto> Settlements);

/// <summary>A settled transaction and what it matched: the receipt (payment) or refund with its number.</summary>
public sealed record SettlementLineDto(
    Guid Id,
    PaymentTransactionType Type,
    string EntityId,
    string? PaymentId,
    string? OrderId,
    string? Method,
    DateTimeOffset? TransactionAt,
    decimal Amount,
    decimal Fee,
    decimal Tax,
    decimal Net,
    SettlementMatch Match,
    decimal? RecordedAmount,
    Guid? ReceiptId,
    string? ReceiptNumber,
    Guid? RefundId,
    string? RefundNumber);

public sealed record GatewaySettlementDetail(GatewaySettlementDto Settlement, IReadOnlyList<SettlementLineDto> Lines);

/// <summary>Ask the gateway for the settlements of one day now (the job does it every hour for today and yesterday).</summary>
public sealed record FetchSettlementsRequest(DateOnly Date);

/// <param name="Added">Settlements stored for the first time.</param>
/// <param name="Updated">Settlements whose status changed (for example created, then processed).</param>
public sealed record SettlementFetchResult(DateOnly Date, int Settlements, int Added, int Updated, string Message);

/// <summary>An exception of the reconciliation, with the documents it concerns (numbers only, never a patient's name).</summary>
/// <param name="CanRecordReceipt">Money without a receipt that HC360 can record against its bill, admission or booking.</param>
public sealed record GatewayDiscrepancyDto(
    Guid Id,
    DiscrepancyKind Kind,
    int Rank,
    DiscrepancyStatus Status,
    string Key,
    string? OrderId,
    string Summary,
    decimal? RecordedAmount,
    decimal? GatewayAmount,
    DateTimeOffset? GatewayAt,
    Guid? BillId,
    string? BillNumber,
    Guid? IpAccountId,
    Guid? ReceiptId,
    string? ReceiptNumber,
    Guid? RefundId,
    string? RefundNumber,
    Guid? AppointmentId,
    string? AppointmentNumber,
    Guid? GatewayOrderRecordId,
    bool CanRecordReceipt,
    DateTimeOffset DetectedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedByName,
    string? Resolution,
    bool ResolvedAutomatically,
    uint Version);

/// <summary>What an action on an exception did, in words for the screen, and the exception as it is now.</summary>
public sealed record DiscrepancyActionResult(string Message, GatewayDiscrepancyDto Discrepancy);

public sealed record ResolveDiscrepancyRequest(string Note, uint Version);

public sealed record DiscrepancyCountDto(DiscrepancyKind Kind, int Count, decimal Amount);

/// <summary>The reconciliation at a glance for the signed-in facility.</summary>
/// <param name="SettlementDays">Online receipts not settled after this many days are listed as exceptions.</param>
public sealed record ReconciliationSummaryDto(
    bool Enabled,
    int SettlementDays,
    IReadOnlyList<DiscrepancyCountDto> Open,
    DateOnly? LastSettledOn,
    DateTimeOffset? LastFetchedAt);

public sealed class FetchSettlementsRequestValidator : AbstractValidator<FetchSettlementsRequest>
{
    public FetchSettlementsRequestValidator()
    {
        RuleFor(x => x.Date).Must(d => d.Year is >= 2020 and <= 2100).WithMessage("Choose the day the gateway paid the bank.");
    }
}

public sealed class ResolveDiscrepancyRequestValidator : AbstractValidator<ResolveDiscrepancyRequest>
{
    public ResolveDiscrepancyRequestValidator()
    {
        RuleFor(x => x.Note).NotEmpty().WithMessage("Say what was done about it.")
            .MinimumLength(5).WithMessage("Say what was done about it in a few words.")
            .SafeText(500);
    }
}
