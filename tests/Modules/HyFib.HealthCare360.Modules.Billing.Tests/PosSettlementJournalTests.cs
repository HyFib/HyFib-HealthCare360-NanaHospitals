using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>Card and UPI settlement lines: the settlement journal in the Tally day book, and an accountant's review.</summary>
public sealed class PosSettlementJournalTests
{
    private static readonly DateOnly Day = new(2026, 10, 7);
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static PosSettlementLine Line(
        PosSettlementLineType type, PaymentMode mode, decimal amount, decimal charges, decimal tax, string? utr = "AXISN1", DateOnly? settledOn = null)
    {
        var gross = type == PosSettlementLineType.Sale ? amount : -amount;
        var row = new PosSettlementRow(
            1, type, mode, "72001234", null, null, amount, charges, tax, gross - charges - tax, "62791" + Random.Shared.Next(1_000_000, 9_999_999), null, null,
            settledOn ?? Day, utr);
        return PosSettlementLine.From(Guid.CreateVersion7(), Guid.CreateVersion7(), "HDFC Bank", row);
    }

    private static TallyDayBook Book(IReadOnlyList<TallyPosSettlement> settlements) =>
        new(Day, [], [], [], [], [], [], []) { PosSettlements = settlements };

    [Fact]
    public void A_payout_clears_the_card_and_upi_ledgers_into_the_bank_less_charges_and_gst()
    {
        var lines = new[]
        {
            Line(PosSettlementLineType.Sale, PaymentMode.Card, 1_500m, 27m, 4.86m),
            Line(PosSettlementLineType.Sale, PaymentMode.Upi, 800m, 0m, 0m),
            Line(PosSettlementLineType.Refund, PaymentMode.Card, 250m, 0m, 0m),
        };

        var journal = PosSettlementJournal.Journals(lines, Day, "NH").ShouldHaveSingleItem();
        var voucher = TallyVoucherBuilder.Build(Book([journal]), new TallyLedgerMap()).ShouldHaveSingleItem();

        voucher.VoucherType.ShouldBe(TallyVoucherBuilder.JournalType);
        voucher.Number.ShouldBe("POS-NH-AXISN1");
        voucher.Reference.ShouldBe("POS-NH-AXISN1");
        voucher.Narration.ShouldBe("Card and UPI settlement, UTR AXISN1");
        Entry(voucher, "Bank").ShouldSatisfyAllConditions(e => e.IsDebit.ShouldBeTrue(), e => e.Amount.ShouldBe(2_018.14m));
        Entry(voucher, "Card & UPI Charges (MDR)").ShouldSatisfyAllConditions(e => e.IsDebit.ShouldBeTrue(), e => e.Amount.ShouldBe(27m));
        Entry(voucher, "Input GST").ShouldSatisfyAllConditions(e => e.IsDebit.ShouldBeTrue(), e => e.Amount.ShouldBe(4.86m));
        Entry(voucher, "Card Settlement Account").ShouldSatisfyAllConditions(e => e.IsDebit.ShouldBeFalse(), e => e.Amount.ShouldBe(1_250m));
        Entry(voucher, "UPI Settlement Account").ShouldSatisfyAllConditions(e => e.IsDebit.ShouldBeFalse(), e => e.Amount.ShouldBe(800m));
        voucher.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
    }

    [Fact]
    public void Each_utr_of_the_day_is_a_journal_of_its_own_and_other_days_are_left_out()
    {
        var lines = new[]
        {
            Line(PosSettlementLineType.Sale, PaymentMode.Card, 100m, 1.5m, 0.27m, "UTR-B"),
            Line(PosSettlementLineType.Sale, PaymentMode.Card, 200m, 3m, 0.54m, "UTR-A"),
            Line(PosSettlementLineType.Sale, PaymentMode.Upi, 50m, 0m, 0m, utr: null),
            Line(PosSettlementLineType.Sale, PaymentMode.Card, 900m, 0m, 0m, "UTR-A", Day.AddDays(-1)),
        };

        var journals = PosSettlementJournal.Journals(lines, Day, "NH");

        journals.Select(j => j.Number).ShouldBe(["POS-NH-20261007", "POS-NH-UTRA", "POS-NH-UTRB"]);
        journals.Single(j => j.Utr == "UTR-A").Net.ShouldBe(196.46m);
        journals.Single(j => j.Utr is null).Clears.ShouldHaveSingleItem().ShouldBe(new TallyPosClearing(PaymentMode.Upi, 50m));
    }

    [Fact]
    public void The_ledgers_are_the_hospitals_own_when_it_names_them()
    {
        var map = new TallyLedgerMap
        {
            PosSettlementBank = "HDFC Current A/c",
            PosCharges = "Bank Charges",
            ModeLedgers = new Dictionary<string, string> { ["Card"] = "HDFC Card Settlement" },
        };
        var journal = PosSettlementJournal.Journals([Line(PosSettlementLineType.Sale, PaymentMode.Card, 1_000m, 18m, 3.24m)], Day, null).Single();

        var voucher = TallyVoucherBuilder.Build(Book([journal]), map).Single();

        voucher.Number.ShouldBe("POS-AXISN1");
        voucher.Entries.Select(e => e.LedgerName).ShouldBe(["HDFC Current A/c", "Bank Charges", "Input GST", "HDFC Card Settlement"], ignoreOrder: true);
    }

    [Fact]
    public void A_day_book_with_only_a_card_payout_is_not_empty()
    {
        Book([]).IsEmpty.ShouldBeTrue();
        Book(PosSettlementJournal.Journals([Line(PosSettlementLineType.Sale, PaymentMode.Card, 10m, 0m, 0m)], Day, "NH")).IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void A_line_that_does_not_match_is_reviewed_once_with_a_note()
    {
        var line = Line(PosSettlementLineType.Sale, PaymentMode.Card, 500m, 0m, 0m);
        line.NeedsAttention.ShouldBeTrue("no receipt records it yet");

        line.Review(Guid.CreateVersion7(), "Accounts", "  Paid at the pharmacy counter; booked there.  ", Now).IsSuccess.ShouldBeTrue();

        line.ShouldSatisfyAllConditions(
            l => l.NeedsAttention.ShouldBeFalse(),
            l => l.ReviewNote.ShouldBe("Paid at the pharmacy counter; booked there."),
            l => l.ReviewedByName.ShouldBe("Accounts"));
        line.Review(Guid.CreateVersion7(), "Accounts", "Again", Now).Error!.Code.ShouldBe("billing.pos_settlement_reviewed");
    }

    [Fact]
    public void A_matched_line_has_nothing_to_review_and_a_different_amount_does()
    {
        var matched = Line(PosSettlementLineType.Sale, PaymentMode.Card, 500m, 0m, 0m);
        matched.MatchReceipt(Guid.CreateVersion7(), Guid.CreateVersion7(), 500m, PosMatchBasis.Rrn, Now);
        var differs = Line(PosSettlementLineType.Sale, PaymentMode.Card, 500m, 0m, 0m);
        differs.MatchReceipt(Guid.CreateVersion7(), Guid.CreateVersion7(), 450m, PosMatchBasis.Rrn, Now);

        matched.ShouldSatisfyAllConditions(l => l.Match.ShouldBe(PosSettlementMatch.Matched), l => l.NeedsAttention.ShouldBeFalse());
        matched.Review(Guid.CreateVersion7(), "Accounts", "Nothing to do", Now).Error!.Code.ShouldBe("billing.pos_settlement_matched");
        differs.ShouldSatisfyAllConditions(l => l.Match.ShouldBe(PosSettlementMatch.AmountDiffers), l => l.NeedsAttention.ShouldBeTrue());
    }

    [Fact]
    public void A_chargeback_is_for_review_and_a_line_of_another_counter_is_not()
    {
        var chargeback = Line(PosSettlementLineType.Adjustment, PaymentMode.Card, 1_200m, 0m, 0m);
        var pharmacy = Line(PosSettlementLineType.Sale, PaymentMode.Card, 300m, 0m, 0m);
        pharmacy.MarkOtherCounter();

        chargeback.ShouldSatisfyAllConditions(l => l.Match.ShouldBe(PosSettlementMatch.Review), l => l.NeedsAttention.ShouldBeTrue(), l => l.Gross.ShouldBe(-1_200m));
        pharmacy.ShouldSatisfyAllConditions(l => l.Match.ShouldBe(PosSettlementMatch.OtherCounter), l => l.NeedsAttention.ShouldBeFalse());
        pharmacy.Unmatch();
        pharmacy.Match.ShouldBe(PosSettlementMatch.NoReceipt);
    }

    [Fact]
    public void Other_counters_are_named_by_the_acquirers_terminal_id_in_any_case()
    {
        var settings = new PosSettlementSettings { OtherTerminals = ["ph000001", " 72009999 "] };

        settings.IsOtherTerminal("PH000001").ShouldBeTrue();
        settings.IsOtherTerminal("72009999").ShouldBeTrue();
        settings.IsOtherTerminal("72001234").ShouldBeFalse();
        settings.IsOtherTerminal(null).ShouldBeFalse();
        new PosSettlementSettings { SettlementDays = 0 }.EffectiveSettlementDays.ShouldBe(1);
    }

    private static AccountingEntry Entry(AccountingVoucher voucher, string ledger) => voucher.Entries.Single(e => e.LedgerName == ledger);
}
