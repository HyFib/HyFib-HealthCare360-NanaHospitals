using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// Reversals in the Tally day book (audit P6): each is a Journal on the day it is made, the entries of its receipt or
/// refund turned round, numbered by the reversal; and the records check compares them with the day-end's.
/// </summary>
public sealed class TallyReversalTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly TallyLedgerMap Ledgers = new();

    private static TallyDayBook Book(IReadOnlyList<TallyReceiptReversal>? receipts = null, IReadOnlyList<TallyRefundReversal>? refunds = null) =>
        new(Day, [], [], [], [], [], [], []) { ReceiptReversals = receipts ?? [], RefundReversals = refunds ?? [] };

    [Fact]
    public void Money_not_received_turns_the_receipt_round_by_mode_in_a_journal_of_its_own()
    {
        var reversal = new TallyReceiptReversal(
            "NHRR260000001", Day, "NHRC260000123", ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Cheque, 600m), new TallyPayment(PaymentMode.Upi, 150m)]);
        var receipt = new TallyReceipt("NHRC260000123", Day, ReceiptPurpose.BillPayment, null, reversal.Payments);

        var voucher = TallyVoucherBuilder.Build(Book([reversal]), Ledgers).ShouldHaveSingleItem();
        var original = TallyVoucherBuilder.Build(new TallyDayBook(Day, [], [receipt], [], [], [], [], []), Ledgers).ShouldHaveSingleItem();

        (voucher.VoucherType, voucher.Number, voucher.Reference).ShouldBe((TallyVoucherBuilder.JournalType, "NHRR260000001", "NHRR260000001"));
        voucher.Narration.ShouldBe("Receipt NHRC260000123 reversed, NHRR260000001: money not received");
        voucher.Entries.Select(e => (e.LedgerName, e.Amount, !e.IsDebit)).Order().ToArray()
            .ShouldBe(original.Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray(), "every entry of the receipt, on the other side");
        voucher.Entries.Single(e => e.LedgerName == Ledgers.PatientDebtors).ShouldSatisfyAllConditions(e => e.IsDebit.ShouldBeTrue(), e => e.Amount.ShouldBe(750m));
    }

    [Fact]
    public void An_advance_not_received_takes_back_from_the_advances_ledger_and_its_undone_adjustment_from_the_debtors()
    {
        var money = new TallyReceiptReversal("NHRR260000002", Day, "NHRC260000124", ReceiptPurpose.Advance, null, [new TallyPayment(PaymentMode.PaymentGateway, 800m)]);
        var undone = new TallyReceiptReversal("NHRR260000003", Day, "NHRC260000125", ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Advance, 100m)]);

        var vouchers = TallyVoucherBuilder.Build(Book([money, undone]), Ledgers);

        var first = vouchers.Single(v => v.Number == money.Number);
        first.Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray().ShouldBe(new[]
        {
            (Ledgers.PatientAdvances, 800m, true), (Ledgers.ModeLedger(PaymentMode.PaymentGateway), 800m, false),
        }.Order().ToArray());
        var second = vouchers.Single(v => v.Number == undone.Number);
        second.Narration.ShouldBe("Advance adjustment of receipt NHRC260000125 undone, NHRR260000003: the advance was not received");
        second.Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray().ShouldBe(new[]
        {
            (Ledgers.PatientDebtors, 100m, true), (Ledgers.ModeLedger(PaymentMode.Advance), 100m, false),
        }.Order().ToArray());
        TallyTracking.DuplicateKeys(vouchers).ShouldBeEmpty();
    }

    [Fact]
    public void A_refund_not_paid_out_turns_its_payment_round()
    {
        var refund = new TallyRefund("NHRF26000001", Day, RefundPurpose.Deposit, PaymentMode.NetBanking, 2_000m);
        var reversal = new TallyRefundReversal("NHRX260000001", Day, "NHRF26000001", RefundPurpose.Deposit, PaymentMode.NetBanking, 2_000m);

        var voucher = TallyVoucherBuilder.Build(Book(refunds: [reversal]), Ledgers).ShouldHaveSingleItem();
        var original = TallyVoucherBuilder.Build(new TallyDayBook(Day, [], [], [], [refund], [], [], []), Ledgers).ShouldHaveSingleItem();

        (voucher.VoucherType, voucher.Number, voucher.Narration).ShouldBe((TallyVoucherBuilder.JournalType, "NHRX260000001", "Refund NHRF26000001 not paid out, NHRX260000001"));
        voucher.Entries.Select(e => (e.LedgerName, e.Amount, !e.IsDebit)).Order().ToArray()
            .ShouldBe(original.Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray());
        Book(refunds: [reversal]).IsEmpty.ShouldBeFalse();
        Book().IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void The_records_check_compares_the_days_reversals_with_the_day_end_by_mode()
    {
        var book = Book(
            [
                new TallyReceiptReversal("NHRR260000001", Day, "NHRC260000123", ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Cheque, 600m)]),
                new TallyReceiptReversal("NHRR260000002", Day, "NHRC260000125", ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Advance, 100m)]),
            ],
            [new TallyRefundReversal("NHRX260000001", Day, "NHRF26000001", RefundPurpose.Bill, PaymentMode.NetBanking, 250m)]);
        var records = new TallyRecordFigures
        {
            NotReceived = [new TallyModeAmount(PaymentMode.Cheque, 600m)],
            NotPaidOut = [new TallyModeAmount(PaymentMode.NetBanking, 250m)],
            AdjustmentsUndone = 100m,
        };

        var lines = TallyRecordsCheck.Compare(records, book, Ledgers).Lines;

        lines.Single(l => l.Group == "Money not received").ShouldSatisfyAllConditions(l => l.Item.ShouldBe("Cheque"), l => l.Matches.ShouldBeTrue());
        lines.Single(l => l.Group == "Refunds not paid out").ShouldSatisfyAllConditions(l => l.Item.ShouldBe("Net banking"), l => l.Matches.ShouldBeTrue());
        lines.Single(l => l.Item == "Adjustments undone (advance not received)").Matches.ShouldBeTrue();

        var missing = TallyRecordsCheck.Compare(records with { NotReceived = [] }, book, Ledgers).Lines.Single(l => l.Group == "Money not received");
        (missing.Matches, missing.Difference).ShouldBe((false, 600m));
        TallyRecordsCheck.Compare(new TallyRecordFigures(), Book(), Ledgers).Lines.ShouldNotContain(l => l.Group == "Money not received" || l.Group == "Refunds not paid out");
    }
}
