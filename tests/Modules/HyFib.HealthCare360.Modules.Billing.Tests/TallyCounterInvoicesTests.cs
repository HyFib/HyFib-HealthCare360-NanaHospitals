using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// One voucher per pharmacy invoice (T17: "one voucher per invoice"): each invoice a Pharmacy Sales voucher numbered by
/// the invoice, each return a Credit Note numbered by the return, each balance paid a Receipt; the daily summary stays an
/// option, and the canteen keeps its summary.
/// </summary>
public sealed class TallyCounterInvoicesTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly DateTimeOffset Noon = new(2026, 9, 25, 6, 30, 0, TimeSpan.Zero);
    private static readonly TallyLedgerMap Ledgers = new();
    private static readonly Guid Ravi = Guid.CreateVersion7();
    private static readonly Guid Asha = Guid.CreateVersion7();

    private static readonly CounterInvoicesDay Invoices = new(
        TallyCounters.Pharmacy,
        Day,
        [
            new CounterInvoice("NHPH260000001", Noon, Ravi, null, null,
                [new CounterTaxLine(12m, 1_000m, 60m, 60m), new CounterTaxLine(5m, 400m, 10m, 10m)], 0.40m,
                [new CounterPayment(CounterPaymentMode.Cash, 1_000m), new CounterPayment(CounterPaymentMode.Upi, 140.40m, "UPI-628812345678")]),
            new CounterInvoice("NHPH260000002", Noon, null, "Ramesh", null, [new CounterTaxLine(12m, 100m, 6m, 6m)], 0m, [new CounterPayment(CounterPaymentMode.Cash, 112m)]),
            new CounterInvoice("NHPH260000003", Noon, Asha, null, null, [new CounterTaxLine(5m, 200m, 5m, 5m)], 0m, []),
        ],
        [
            new CounterReturn("NHPR260000001", Noon, "NHPH260000001", Ravi, null, null, [new CounterTaxLine(12m, 100m, 6m, 6m)], [new CounterPayment(CounterPaymentMode.Cash, 62m)]),
        ],
        [new CounterCollection("NHPH250000077", Asha, null, [new CounterPayment(CounterPaymentMode.Card, 150m, "627910123456")])]);

    /// <summary>The same day summed, as the pharmacy's summary source gives it.</summary>
    private static readonly CounterSalesDay Summary = new(
        TallyCounters.Pharmacy,
        Day,
        new CounterDocuments(3, [new CounterTaxLine(12m, 1_100m, 66m, 66m), new CounterTaxLine(5m, 600m, 15m, 15m)], 0.40m,
            [new CounterMoney(CounterPaymentMode.Cash, 1_112m), new CounterMoney(CounterPaymentMode.Upi, 140.40m)]),
        new CounterDocuments(1, [new CounterTaxLine(12m, 100m, 6m, 6m)], 0m, [new CounterMoney(CounterPaymentMode.Cash, 62m)]),
        [new CounterMoney(CounterPaymentMode.Card, 150m)]);

    private static TallyDayBook Book(params CounterSalesDay[] counters) => new TallyDayBook(Day, [], [], [], [], [], [], []) with
    {
        FacilityCode = "NH",
        Counters = counters,
        CounterInvoices = [Invoices],
        Patients = new Dictionary<Guid, TallyPatient>
        {
            [Ravi] = TallyPatient.Registered("NHU26000123", "Ravi Kumar", isVip: false, SensitiveCategory.None),
            [Asha] = TallyPatient.Registered("NHU26000124", "Asha Devi", isVip: false, SensitiveCategory.Hiv),
        },
    };

    private static AccountingEntry Entry(AccountingVoucher voucher, string ledger) => voucher.Entries.Single(e => e.LedgerName == ledger);

    private static void ShouldBalance(AccountingVoucher voucher) =>
        voucher.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount), voucher.Number);

    [Fact]
    public void Each_invoice_return_and_balance_paid_is_a_voucher_of_its_own_and_the_daily_summary_is_not_posted()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(Summary), Ledgers);

        vouchers.Select(v => (v.VoucherType, v.Number, v.Reference)).ShouldBe(
        [
            ("Pharmacy Sales", "NHPH260000001", "NHPH260000001"),
            ("Pharmacy Sales", "NHPH260000002", "NHPH260000002"),
            ("Pharmacy Sales", "NHPH260000003", "NHPH260000003"),
            (TallyVoucherBuilder.CreditNoteType, "NHPR260000001", "NHPR260000001"),
            (TallyVoucherBuilder.ReceiptType, "NHPH250000077/20260925", "NHPH250000077/20260925"),
        ]);
        vouchers.ToList().ForEach(ShouldBalance);
        vouchers.ShouldNotContain(v => v.Number.StartsWith("PH-", StringComparison.Ordinal));
    }

    [Fact]
    public void An_invoice_debits_the_money_by_mode_and_the_account_and_credits_income_by_rate_gst_and_round_off()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(Summary), Ledgers);

        var first = vouchers.Single(v => v.Number == "NHPH260000001");
        Entry(first, "Cash").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(1_000m), e => e.IsDebit.ShouldBeTrue());
        Entry(first, "UPI Settlement Account").Amount.ShouldBe(140.40m);
        Entry(first, "Pharmacy Receivables").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(400m), e => e.IsDebit.ShouldBeTrue());
        Entry(first, "Pharmacy Sales @ 12%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(1_000m), e => e.IsDebit.ShouldBeFalse());
        Entry(first, "Pharmacy Sales @ 5%").Amount.ShouldBe(400m);
        Entry(first, "Round Off").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(0.40m), e => e.IsDebit.ShouldBeFalse());

        var onCredit = vouchers.Single(v => v.Number == "NHPH260000003");
        Entry(onCredit, "Pharmacy Receivables").Amount.ShouldBe(210m);
    }

    [Fact]
    public void A_return_is_the_sale_turned_round_and_a_balance_paid_clears_the_account()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(Summary), Ledgers);

        var credit = vouchers.Single(v => v.Number == "NHPR260000001");
        Entry(credit, "Pharmacy Sales @ 12%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(100m), e => e.IsDebit.ShouldBeTrue());
        Entry(credit, "Cash").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(62m), e => e.IsDebit.ShouldBeFalse());
        Entry(credit, "Pharmacy Receivables").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(50m), e => e.IsDebit.ShouldBeFalse());

        var receipt = vouchers.Single(v => v.Number == "NHPH250000077/20260925");
        Entry(receipt, "Card Settlement Account").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(150m), e => e.IsDebit.ShouldBeTrue());
        Entry(receipt, "Pharmacy Receivables").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(150m), e => e.IsDebit.ShouldBeFalse());
    }

    [Fact]
    public void Narrations_name_the_patient_a_walk_in_customer_or_only_the_uhid_of_a_protected_patient()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(Summary), Ledgers).ToDictionary(v => v.Number);

        vouchers["NHPH260000001"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Pharmacy invoice NHPH260000001");
        vouchers["NHPH260000002"].Narration.ShouldBe("Ramesh: Pharmacy invoice NHPH260000002");
        vouchers["NHPH260000003"].Narration.ShouldBe("UHID NHU26000124: Pharmacy invoice NHPH260000003");
        vouchers["NHPR260000001"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Return NHPR260000001 of pharmacy invoice NHPH260000001");
        vouchers["NHPH250000077/20260925"].Narration.ShouldBe("UHID NHU26000124: Balance of pharmacy invoice NHPH250000077 collected");
    }

    [Fact]
    public void A_facility_can_keep_the_pharmacys_daily_summary()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(Summary), Ledgers with { PharmacyPerInvoice = false });

        vouchers.Select(v => (v.VoucherType, v.Number)).ShouldBe(
        [
            ("Pharmacy Sales", "PH-NH-20260925"),
            (TallyVoucherBuilder.CreditNoteType, "PH-NH-20260925-R"),
            (TallyVoucherBuilder.ReceiptType, "PH-NH-20260925-C"),
        ]);
    }

    [Fact]
    public void Both_ways_post_the_same_amounts_to_the_same_ledgers()
    {
        static Dictionary<string, decimal> Net(IEnumerable<AccountingVoucher> vouchers) =>
            vouchers.SelectMany(v => v.Entries).GroupBy(e => e.LedgerName).ToDictionary(g => g.Key, g => g.Sum(e => e.IsDebit ? e.Amount : -e.Amount));

        var perInvoice = Net(TallyVoucherBuilder.Build(Book(Summary), Ledgers));
        var summarised = Net(TallyVoucherBuilder.Build(Book(Summary), Ledgers with { PharmacyPerInvoice = false }));

        perInvoice.ShouldBe(summarised, ignoreOrder: true);
    }

    [Fact]
    public void A_pharmacy_switched_off_posts_neither_and_the_canteen_keeps_its_summary()
    {
        var canteen = new CounterSalesDay(TallyCounters.Canteen, Day,
            new CounterDocuments(40, [new CounterTaxLine(5m, 2_000m, 50m, 50m)], 0m, [new CounterMoney(CounterPaymentMode.Cash, 2_100m)]), CounterDocuments.None, []);

        var vouchers = TallyVoucherBuilder.Build(Book(Summary, canteen), Ledgers with { Pharmacy = new TallyCounterLedgers { Include = false } });

        vouchers.Select(v => (v.VoucherType, v.Number)).ShouldBe([(TallyVoucherBuilder.SalesType, "CT-NH-20260925")]);
    }

    [Fact]
    public void Without_the_invoices_the_summary_is_posted_as_before()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(Summary) with { CounterInvoices = [] }, Ledgers);

        vouchers.Select(v => v.Number).ShouldBe(["PH-NH-20260925", "PH-NH-20260925-R", "PH-NH-20260925-C"]);
    }

    [Fact]
    public void A_upi_payment_into_a_bank_ledger_carries_its_reference()
    {
        var upiToBank = Ledgers with { Pharmacy = new TallyCounterLedgers { ModeLedgers = new Dictionary<string, string> { ["Upi"] = "Bank" } } };

        var voucher = TallyVoucherBuilder.Build(Book(Summary), upiToBank).Single(v => v.Number == "NHPH260000001");

        Entry(voucher, "Bank").BankAllocations.ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.EFundTransfer, 140.40m, "UPI-628812345678")]);
        Entry(voucher, "Cash").BankAllocations.ShouldBeEmpty();
    }

    [Fact]
    public void The_receipt_of_a_balance_paid_is_numbered_by_the_invoice_and_the_day() =>
        TallyCounterInvoices.CollectionNumber(" NHPH260000001 ", Day).ShouldBe("NHPH260000001/20260925");

    [Fact]
    public void Per_invoice_is_the_default() => new TallyLedgerMap().PharmacyPerInvoice.ShouldBeTrue();
}
