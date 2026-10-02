using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class TallyRecordsCheckTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);

    private static readonly TallyDayBook Book = new(
        Day,
        [
            new TallySale("NHOB260000001", Day, null, 1_180m, 0m, 0m, [new TallySaleLine(null, null, 1_000m, 0m, 90m, 90m)], false),
            new TallySale("NHOB260000002", Day, "Star Health", 200m, 800m, 0m, [new TallySaleLine(null, null, 1_000m, 0m, 0m, 0m)], false),
            new TallySale("NHOB260000002", Day, "Star Health", 200m, 800m, 0m, [new TallySaleLine(null, null, 1_000m, 0m, 0m, 0m)], true),
        ],
        [
            new TallyReceipt("RC1", Day, ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Cash, 700m), new TallyPayment(PaymentMode.Upi, 480m)]),
            new TallyReceipt("RC2", Day, ReceiptPurpose.Deposit, null, [new TallyPayment(PaymentMode.Card, 5_000m)]),
            new TallyReceipt("RC3", Day, ReceiptPurpose.PayerSettlement, "Star Health", [new TallyPayment(PaymentMode.Insurance, 45_000m)]),
            new TallyReceipt("RC4", Day, ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Advance, 800m)]),
        ],
        [new TallyDisallowance("NHCL26000001/0198a1", Day, "Star Health", 3_000m)],
        [new TallyRefund("RF1", Day, RefundPurpose.Bill, PaymentMode.Cash, 300m), new TallyRefund("RF2", Day, RefundPurpose.Bill, PaymentMode.Advance, 200m)],
        [new TallyCreditNote("CN1", Day, CreditNoteKind.CreditNote, 150m), new TallyCreditNote("CN2", Day, CreditNoteKind.WriteOff, 50m)],
        [new TallyDoctorFee("Dr. Arjun Mehta", 960m, "DR005")],
        []);

    /// <summary>The records as they agree with the day book above.</summary>
    private static readonly TallyRecordFigures Records = new()
    {
        BillsFinalised = 2,
        BillsFinalisedAmount = 2_180m,
        BillsCancelled = 1,
        BillsCancelledAmount = 1_000m,
        DayEnd = [new ShiftModeTotal(PaymentMode.Cash, 700m, 300m), new ShiftModeTotal(PaymentMode.Card, 5_000m, 0m), new ShiftModeTotal(PaymentMode.Upi, 480m, 0m)],
        AdvancesCollected = 5_000m,
        AdvancesAdjusted = 800m,
        PayerSettlements = 45_000m,
        CreditNotes = 1,
        CreditNotesAmount = 150m,
        WriteOffs = 1,
        WriteOffsAmount = 50m,
        ClaimDeductions = 3_000m,
        DoctorFeesAccrued = 960m,
    };

    private static TallyCheckLine Line(TallyRecordsComparison comparison, string group, string item) =>
        comparison.Lines.Single(l => l.Group == group && l.Item == item);

    [Fact]
    public void A_day_book_that_agrees_with_its_records_has_no_differences()
    {
        var comparison = TallyRecordsCheck.Compare(Records, Book, new TallyLedgerMap());

        comparison.Lines.Where(l => !l.Matches).ShouldBeEmpty();
        comparison.Differences.ShouldBe(0);
        comparison.Notes.ShouldBeEmpty();
        Line(comparison, "Bills", "Finalised").ShouldSatisfyAllConditions(l => l.DayBook.ShouldBe(2_180m), l => l.DayBookCount.ShouldBe(2));
        Line(comparison, "Money received", "UPI").DayBook.ShouldBe(480m);
        Line(comparison, "Money paid back", "Cash").DayBook.ShouldBe(300m);
    }

    [Fact]
    public void Money_received_is_compared_per_mode_with_the_cashier_day_end_without_insurers_or_adjustments()
    {
        var comparison = TallyRecordsCheck.Compare(Records with { DayEnd = [new ShiftModeTotal(PaymentMode.Cash, 650m, 300m), .. Records.DayEnd.Skip(1)] }, Book, new TallyLedgerMap());

        var cash = Line(comparison, "Money received", "Cash");
        cash.ShouldSatisfyAllConditions(l => l.Records.ShouldBe(650m), l => l.DayBook.ShouldBe(700m), l => l.Difference.ShouldBe(50m), l => l.Matches.ShouldBeFalse());
        comparison.Lines.ShouldNotContain(l => l.Group == "Money received" && (l.Item == "Insurance" || l.Item == "Advance"));
        comparison.Differences.ShouldBe(1);
    }

    [Fact]
    public void A_bill_missing_from_the_day_book_shows_in_the_count()
    {
        var comparison = TallyRecordsCheck.Compare(Records with { BillsFinalised = 3, BillsFinalisedAmount = 2_180m }, Book, new TallyLedgerMap());

        Line(comparison, "Bills", "Finalised").Matches.ShouldBeFalse();
    }

    [Fact]
    public void Open_shifts_and_cash_variances_are_listed_for_the_accountant()
    {
        var comparison = TallyRecordsCheck.Compare(Records with { OpenShifts = 1, CashVariances = [new TallyCashVariance("OPD-1", "Asha", -120m)] }, Book, new TallyLedgerMap());

        comparison.Notes.ShouldBe([
            "1 cashier shift is still open, so the day-end figures may still change.",
            "Cash was short by ₹120.00 at OPD-1 (Asha). The day book has no voucher for it: record it in Tally.",
        ]);
        comparison.Differences.ShouldBe(0);
    }

    [Fact]
    public void Counter_days_are_listed_and_one_left_out_on_purpose_is_not_a_difference()
    {
        var pharmacy = new CounterSalesDay(TallyCounters.Pharmacy, Day,
            new CounterDocuments(2, [new CounterTaxLine(12m, 100m, 6m, 6m)], 0m, [new CounterMoney(CounterPaymentMode.Cash, 112m)]), CounterDocuments.None, []);
        var map = new TallyLedgerMap { Pharmacy = new TallyCounterLedgers { Include = false } };

        var comparison = TallyRecordsCheck.Compare(Records, Book with { Counters = [pharmacy] }, map);

        var sales = Line(comparison, "Pharmacy", "Sales");
        sales.ShouldSatisfyAllConditions(l => l.Records.ShouldBe(112m), l => l.DayBook.ShouldBe(0m), l => l.Matches.ShouldBeTrue(), l => l.Note.ShouldNotBeNull());
        comparison.Differences.ShouldBe(0);
    }
}
