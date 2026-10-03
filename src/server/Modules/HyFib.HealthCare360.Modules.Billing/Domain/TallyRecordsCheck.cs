using System.Globalization;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>One figure of a day as HC360's records have it and as the day book has it.</summary>
/// <param name="Informational">
/// Totals only, never a difference: the day book takes the figure from the only record HC360 has of it (a counter's
/// day), so there is nothing independent to compare it with. <paramref name="Records"/> is that record's figure.
/// </param>
public sealed record TallyCheckLine(
    string Group, string Item, decimal Records, decimal DayBook, int? RecordsCount = null, int? DayBookCount = null, string? Note = null, bool Informational = false)
{
    public decimal Difference => DayBook - Records;

    public bool Matches => Informational || (Difference == 0 && RecordsCount == DayBookCount);
}

/// <summary>A cashier shift's day-end variance: declared cash minus expected (negative is a shortage).</summary>
public sealed record TallyCashVariance(string Counter, string Cashier, decimal Variance);

/// <summary>
/// A day's figures read from HC360's records, independently of the day book: bills, the cashier day-end by payment
/// mode, settlements, credit notes and write-offs, claim deductions and doctor fees.
/// </summary>
public sealed record TallyRecordFigures
{
    public int BillsFinalised { get; init; }
    public decimal BillsFinalisedAmount { get; init; }
    public int BillsCancelled { get; init; }
    public decimal BillsCancelledAmount { get; init; }

    /// <summary>The cashier day-end report: collected and refunded per mode (advance adjustments and insurers excluded).</summary>
    public IReadOnlyList<ShiftModeTotal> DayEnd { get; init; } = [];

    /// <summary>The day-end's in-patient deposits and patient advances received.</summary>
    public decimal AdvancesCollected { get; init; }

    public decimal AdvancesAdjusted { get; init; }
    public decimal PayerSettlements { get; init; }
    public int CreditNotes { get; init; }
    public decimal CreditNotesAmount { get; init; }
    public int WriteOffs { get; init; }
    public decimal WriteOffsAmount { get; init; }
    public decimal ClaimDeductions { get; init; }
    public decimal DoctorFeesAccrued { get; init; }
    public int Payouts { get; init; }
    public decimal PayoutsNet { get; init; }
    public int OpenShifts { get; init; }
    public IReadOnlyList<TallyCashVariance> CashVariances { get; init; } = [];
}

/// <summary>The day book compared with its records (INT-TALLY reconciliation inside HC360).</summary>
public sealed record TallyRecordsComparison(IReadOnlyList<TallyCheckLine> Lines, IReadOnlyList<string> Notes)
{
    public int Differences => Lines.Count(l => !l.Matches);
}

/// <summary>
/// Compares a day book with the records it is built from, figure by figure. Money received and paid back is compared
/// per payment mode with the cashier day-end report (OPB-07), which works from shifts rather than from the day. The
/// pharmacy and canteen days are listed as totals only: the day book is built from the counter's own day, the only
/// record HC360 has of those sales, so comparing the two could never find a difference. Cash shortages and excesses at day-end
/// are not in the day book; they are listed so the accountant can record them in Tally.
/// </summary>
public static class TallyRecordsCheck
{
    private const string Bills = "Bills";
    private const string Received = "Money received";
    private const string PaidBack = "Money paid back";
    private const string Advances = "Advances";
    private const string Insurance = "Insurance";
    private const string Credits = "Credit notes";
    private const string DoctorFees = "Doctor fees";

    public static TallyRecordsComparison Compare(TallyRecordFigures records, TallyDayBook day, TallyLedgerMap map)
    {
        var lines = new List<TallyCheckLine>();
        var sales = day.Sales.Where(s => !s.IsCancellation).ToList();
        var cancellations = day.Sales.Where(s => s.IsCancellation).ToList();
        lines.Add(new(Bills, "Finalised", records.BillsFinalisedAmount, sales.Sum(s => s.PatientAmount + s.PayerAmount), records.BillsFinalised, sales.Count));
        lines.Add(new(Bills, "Cancelled", records.BillsCancelledAmount, cancellations.Sum(s => s.PatientAmount + s.PayerAmount), records.BillsCancelled, cancellations.Count));

        // The day-end counts receipts other than insurer settlements, and leaves out advance adjustments and insurers.
        var counterModes = day.Receipts.Where(r => r.Purpose != ReceiptPurpose.PayerSettlement)
            .SelectMany(r => r.Payments)
            .Where(p => p.Mode is not (PaymentMode.Advance or PaymentMode.Insurance))
            .GroupBy(p => p.Mode)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var refundModes = day.Refunds.Where(r => r.Mode is not (PaymentMode.Advance or PaymentMode.Insurance))
            .GroupBy(r => r.Mode)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var dayEnd = records.DayEnd.ToDictionary(t => t.Mode);
        foreach (var mode in counterModes.Keys.Union(refundModes.Keys).Union(dayEnd.Keys).Order())
        {
            var fromDayEnd = dayEnd.GetValueOrDefault(mode);
            if ((fromDayEnd?.Collected ?? 0) != 0 || counterModes.ContainsKey(mode))
            {
                lines.Add(new(Received, ModeName(mode), fromDayEnd?.Collected ?? 0, counterModes.GetValueOrDefault(mode), Note: "Records: the cashier day-end report"));
            }

            if ((fromDayEnd?.Refunded ?? 0) != 0 || refundModes.ContainsKey(mode))
            {
                lines.Add(new(PaidBack, ModeName(mode), fromDayEnd?.Refunded ?? 0, refundModes.GetValueOrDefault(mode), Note: "Records: the cashier day-end report"));
            }
        }

        lines.Add(new(Advances, "Deposits and advances received", records.AdvancesCollected,
            day.Receipts.Where(r => r.Purpose is ReceiptPurpose.Deposit or ReceiptPurpose.Advance).SelectMany(r => r.Payments).Where(p => p.Mode != PaymentMode.Advance).Sum(p => p.Amount)));
        lines.Add(new(Advances, "Adjusted against bills", records.AdvancesAdjusted,
            day.Receipts.SelectMany(r => r.Payments).Where(p => p.Mode == PaymentMode.Advance).Sum(p => p.Amount)));
        lines.Add(new(Insurance, "Settlements received", records.PayerSettlements,
            day.Receipts.Where(r => r.Purpose == ReceiptPurpose.PayerSettlement).SelectMany(r => r.Payments).Sum(p => p.Amount)));
        lines.Add(new(Insurance, "Claim deductions", records.ClaimDeductions, day.Disallowances.Sum(d => d.Amount)));

        var notes = day.CreditNotes.Where(c => c.Kind == CreditNoteKind.CreditNote).ToList();
        var writeOffs = day.CreditNotes.Where(c => c.Kind == CreditNoteKind.WriteOff).ToList();
        lines.Add(new(Credits, "Credit notes", records.CreditNotesAmount, notes.Sum(c => c.Amount), records.CreditNotes, notes.Count));
        lines.Add(new(Credits, "Write-offs", records.WriteOffsAmount, writeOffs.Sum(c => c.Amount), records.WriteOffs, writeOffs.Count));
        lines.Add(new(DoctorFees, "Accrued", records.DoctorFeesAccrued, day.DoctorFees.Sum(f => f.Amount)));
        lines.Add(new(DoctorFees, "Paid out (net of TDS)", records.PayoutsNet, day.Payouts.Sum(p => p.Net), records.Payouts, day.Payouts.Count));

        foreach (var counter in day.Counters.OrderBy(c => c.Counter, StringComparer.Ordinal))
        {
            var name = TallyCounters.NameOf(counter.Counter);
            var included = map.CounterLedgers(counter.Counter).Include;
            var note = included
                ? $"Totals only: the day book takes them from the {name.ToLowerInvariant()}'s own day, "
                  + "which HC360 has no second record of."
                : "Totals only, not in the day book: switched off in the Tally ledger settings.";
            lines.Add(new(name, "Sales", counter.Sales.Total, included ? counter.Sales.Total : 0, counter.Sales.Count, included ? counter.Sales.Count : 0, note, Informational: true));
            lines.Add(new(name, "Returns and cancellations", counter.Returns.Total, included ? counter.Returns.Total : 0, counter.Returns.Count, included ? counter.Returns.Count : 0, note,
                Informational: true));
            var collected = counter.Collected.Sum(c => c.Amount);
            lines.Add(new(name, "Balances collected", collected, included ? collected : 0, Note: note, Informational: true));
        }

        return new TallyRecordsComparison(lines, Notes(records));
    }

    private static List<string> Notes(TallyRecordFigures records)
    {
        var notes = new List<string>();
        if (records.OpenShifts > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{records.OpenShifts} cashier shift{(records.OpenShifts == 1 ? " is" : "s are")} still open, so the day-end figures may still change."));
        }

        foreach (var variance in records.CashVariances.Where(v => v.Variance != 0))
        {
            var kind = variance.Variance < 0 ? "short" : "over";
            notes.Add($"Cash was {kind} by ₹{Money.FormatInr(Math.Abs(variance.Variance))} at {variance.Counter} ({variance.Cashier}). The day book has no voucher for it: record it in Tally.");
        }

        return notes;
    }

    private static string ModeName(PaymentMode mode) => mode switch
    {
        PaymentMode.Upi => "UPI",
        PaymentMode.NetBanking => "Net banking",
        PaymentMode.PaymentGateway => "Payment gateway",
        _ => mode.ToString(),
    };
}
