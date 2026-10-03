using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tally;

/// <summary>A day's posting record: status, Tally's counts, the file import and the two reconciliation checks.</summary>
/// <param name="ConfirmedCount">Vouchers of the current day book Tally holds as they are now (confirmed, or imported from the file).</param>
/// <param name="RecordsCheck">The day book against its records and against what Tally confirmed.</param>
/// <param name="TallyCheck">The vouchers read back from Tally.</param>
public sealed record TallyExportDto(
    Guid Id,
    DateOnly Date,
    TallyExportStatus Status,
    int VoucherCount,
    int Attempts,
    int CreatedCount,
    int AlteredCount,
    int ErrorCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? PostedAt,
    string? Message,
    int ConfirmedCount,
    int RejectedCount,
    DateTimeOffset? FileDownloadedAt,
    DateTimeOffset? ImportedAt,
    string? ImportedByName,
    TallyCheckStatus RecordsCheck,
    DateTimeOffset? RecordsCheckedAt,
    int RecordsDifferences,
    TallyCheckStatus TallyCheck,
    DateTimeOffset? TallyCheckedAt,
    int TallyDifferences,
    string? TallyCheckMessage)
{
    public static TallyExportDto From(TallyExport e) => new(
        e.Id, e.Date, e.Status, e.VoucherCount, e.Attempts, e.CreatedCount, e.AlteredCount, e.ErrorCount, e.CreatedAt, e.LastAttemptAt, e.PostedAt,
        e.Message, e.ConfirmedCount, e.RejectedCount, e.FileDownloadedAt, e.ImportedAt, e.ImportedByName, e.RecordsCheck, e.RecordsCheckedAt,
        e.RecordsDifferences, e.TallyCheck, e.TallyCheckedAt, e.TallyDifferences, e.TallyCheckMessage);
}

/// <summary>One voucher of the day as HC360 sent it, with Tally's answer and the last check against Tally.</summary>
/// <param name="BillDate">
/// For a bill found only in Tally on this day: the day HC360 has it on, to be posted again.
/// </param>
public sealed record TallyVoucherDto(
    string Key,
    string? RemoteId,
    string VoucherType,
    string Number,
    decimal Amount,
    TallyVoucherStatus Status,
    TallyVoucherAnswer Answer,
    string? AnswerMessage,
    DateTimeOffset? AnsweredAt,
    TallyMatch Match,
    decimal? TallyAmount,
    string? TallyNumber,
    string? TallyDetail,
    DateTimeOffset? CheckedAt,
    DateOnly? BillDate = null)
{
    public static TallyVoucherDto From(TallyVoucherRecord r) => new(
        r.Key, r.RemoteId, r.VoucherType, r.Number, r.Amount, r.Status, r.Answer, r.AnswerMessage, r.AnsweredAt, r.Match, r.TallyAmount, r.TallyNumber,
        r.TallyDetail, r.CheckedAt, r.BillDate);
}

/// <summary>The day's vouchers: the day book first, then vouchers that left it and vouchers found only in Tally.</summary>
/// <param name="Status">The day's posting status; null when the day has not been posted.</param>
public sealed record TallyDayVouchersDto(DateOnly Date, TallyExportStatus? Status, TallySending Sending, IReadOnlyList<TallyVoucherDto> Vouchers);

/// <summary>A day's reconciliation: the day book against its records and against what Tally confirmed, and the last check against Tally.</summary>
/// <param name="Records">Matched when every line agrees and Tally holds every voucher of the day book as it is now.</param>
/// <param name="CanCheckTally">Tally is set up for the facility and the day has been posted, so it can be read back.</param>
public sealed record TallyDayCheckDto(
    DateOnly Date,
    TallyExportStatus? Status,
    TallyCheckStatus Records,
    IReadOnlyList<TallyCheckLine> Lines,
    IReadOnlyList<string> Notes,
    TallySending Sending,
    TallyCheckStatus Tally,
    DateTimeOffset? TallyCheckedAt,
    int TallyDifferences,
    string? TallyMessage,
    bool CanCheckTally);

/// <summary>Queue (or re-queue) the day book of a date, or of every date up to <see cref="To"/>, for posting to Tally.</summary>
public sealed record PostDayBookRequest(DateOnly Date, DateOnly? To = null);

public sealed class PostDayBookRequestValidator : AbstractValidator<PostDayBookRequest>
{
    public PostDayBookRequestValidator(TimeProvider time)
    {
        // A day is an Indian calendar day: the UTC date plus one still allows "today" late in the Indian evening.
        RuleFor(x => x.Date)
            .Must(date => date <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1))
            .WithMessage("The day book of a future date cannot be posted.");
        RuleFor(x => x.To)
            .Must((request, to) => to is null || to >= request.Date).WithMessage("The last day must not be before the first.")
            .Must(to => to is null || to <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1)).WithMessage("The day book of a future date cannot be posted.")
            .Must((request, to) => to is null || to.Value.DayNumber - request.Date.DayNumber < TallyService.MaxDaysPerQueue)
            .WithMessage($"Post at most {TallyService.MaxDaysPerQueue} days at a time.");
    }
}

/// <summary>
/// Validates the ledger names an accountant sets for the day book (<c>billing.tally.ledgers</c>): every name present,
/// one line, at most 100 characters (Tally takes longer names, but HC360 keeps them readable), and only known payment
/// modes in the mode maps.
/// </summary>
public sealed class TallyLedgerMapValidator : AbstractValidator<TallyLedgerMap>
{
    private const int MaxName = 100;
    private const int MaxEntries = 200;

    public TallyLedgerMapValidator()
    {
        RuleFor(x => x.DefaultIncome).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.Discount).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.OutputCgst).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.OutputSgst).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.RoundOff).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.PatientDebtors).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.PatientAdvances).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.PayerReceivables).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.CreditNotes).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.WriteOffs).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.InsuranceDisallowances).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.DoctorFeeExpense).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.DoctorPayablePrefix).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.TdsPayable).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.DoctorRecoveries).Must(BeALedgerName).WithMessage(LedgerMessage);
        RuleFor(x => x.PayoutBank).Must(BeALedgerName).WithMessage(LedgerMessage);

        RuleFor(x => x.IncomeByDepartment).Must(map => map is null || map.Count <= MaxEntries).WithMessage($"Map at most {MaxEntries} departments.");
        RuleForEach(x => x.IncomeByDepartment).Must(pair => BeACode(pair.Key) && BeALedgerName(pair.Value))
            .WithMessage("Each department needs its code (letters and digits, at most 20) and a ledger name of at most 100 characters.");
        RuleForEach(x => x.ModeLedgers).Must(pair => Enum.GetNames<PaymentMode>().Contains(pair.Key, StringComparer.OrdinalIgnoreCase) && BeALedgerName(pair.Value))
            .WithMessage("Each payment mode needs a known mode and a ledger name of at most 100 characters.");
        RuleFor(x => x.Pharmacy).NotNull().SetValidator(new CounterValidator());
        RuleFor(x => x.Canteen).NotNull().SetValidator(new CounterValidator());
    }

    private static string LedgerMessage => $"Enter the ledger name as it is in Tally, on one line and at most {MaxName} characters.";

    internal static bool BeALedgerName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxName && !value.Any(char.IsControl);

    private static bool BeACode(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 20 && value.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private sealed class CounterValidator : AbstractValidator<TallyCounterLedgers>
    {
        public CounterValidator()
        {
            RuleFor(x => x.Sales).Must(v => v is null || BeALedgerName(v)).WithMessage(LedgerMessage);
            RuleFor(x => x.OnAccount).Must(v => v is null || BeALedgerName(v)).WithMessage(LedgerMessage);
            RuleForEach(x => x.ModeLedgers).Must(pair => Enum.GetNames<CounterPaymentMode>().Contains(pair.Key, StringComparer.OrdinalIgnoreCase) && BeALedgerName(pair.Value))
                .WithMessage("Each payment mode needs Cash, Card or Upi and a ledger name of at most 100 characters.");
        }
    }
}
