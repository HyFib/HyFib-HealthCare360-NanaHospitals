using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Shifts;

public sealed record OpenShiftRequest(string Counter, decimal OpeningFloat);

public sealed record CloseShiftRequest(decimal DeclaredCash, string? Notes, uint Version);

public sealed record ShiftModeTotalDto(PaymentMode Mode, decimal Collected, decimal Refunded, decimal Net);

public sealed record ShiftDto(
    Guid Id,
    Guid UserId,
    string UserName,
    string Counter,
    ShiftStatus Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal OpeningFloat,
    decimal ExpectedCash,
    decimal? DeclaredCash,
    decimal? Variance,
    IReadOnlyList<ShiftModeTotalDto> Totals,
    int ReceiptCount,
    string? Notes,
    uint Version);

public sealed record DayEndCashierRow(
    Guid ShiftId,
    Guid UserId,
    string UserName,
    string Counter,
    ShiftStatus Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal Collected,
    decimal Refunded,
    decimal ExpectedCash,
    decimal? DeclaredCash,
    decimal? Variance);

/// <summary>Money recorded on an earlier receipt or refund that did not move, reversed on the day, in one payment mode.</summary>
public sealed record DayEndReversalTotal(PaymentMode Mode, decimal Amount, int Count);

/// <summary>
/// Day-end collection report (OPB-07): per cashier/counter, per payment mode, and the day's billing totals. The
/// reversals made on the day (money not received, refunds not paid out) are made by the accounts office, not at a
/// counter: they are in the whole day's report, and <paramref name="NetAfterReversals"/> is the net collection with them.
/// </summary>
public sealed record DayEndReport(
    DateOnly Date,
    string? Counter,
    IReadOnlyList<DayEndCashierRow> Shifts,
    IReadOnlyList<ShiftModeTotalDto> ByMode,
    decimal TotalCollected,
    decimal TotalRefunded,
    decimal NetCollection,
    decimal DepositsCollected,
    int BillsFinalized,
    decimal BilledAmount,
    decimal DiscountGiven,
    int BillsCancelled,
    decimal CancelledAmount,
    int OpenShifts,
    IReadOnlyList<DayEndReversalTotal>? NotReceived = null,
    IReadOnlyList<DayEndReversalTotal>? NotPaidOut = null,
    decimal NetAfterReversals = 0m);

public sealed class OpenShiftRequestValidator : AbstractValidator<OpenShiftRequest>
{
    public OpenShiftRequestValidator()
    {
        RuleFor(x => x.Counter).NotEmpty().WithMessage("Enter the counter name.").MaximumLength(64);
        RuleFor(x => x.OpeningFloat).NonNegativeAmount();
    }
}

public sealed class CloseShiftRequestValidator : AbstractValidator<CloseShiftRequest>
{
    public CloseShiftRequestValidator()
    {
        RuleFor(x => x.DeclaredCash).NonNegativeAmount();
        RuleFor(x => x.Notes).SafeText(500);
    }
}
