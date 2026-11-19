using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// GST by rate and the party's GST details (T13: "CGST, SGST and IGST, ... rate of tax and party GST details which should
/// match its GSTR2B"): each tax to a ledger per rate named by the tax's own rate, the single ledger where a document has
/// no rate, and the GSTIN, state and place of supply of a B2B buyer. Every voucher still balances to the paisa.
/// </summary>
public sealed class TallyGstTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly DateTimeOffset Noon = new(2026, 9, 25, 6, 30, 0, TimeSpan.Zero);
    private static readonly TallyLedgerMap Ledgers = new();

    private static TallyDayBook Book(IReadOnlyList<TallySale>? sales = null, IReadOnlyList<TallyCreditNote>? creditNotes = null) =>
        new(Day, sales ?? [], [], [], [], creditNotes ?? [], [], []);

    private static AccountingEntry Entry(AccountingVoucher voucher, string ledger) => voucher.Entries.Single(e => e.LedgerName == ledger);

    private static void ShouldBalance(AccountingVoucher voucher) =>
        voucher.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount), voucher.Number);

    [Theory]
    [InlineData(5, "Output CGST @ 2.5%", "Output SGST @ 2.5%", "Output IGST @ 5%")]
    [InlineData(12, "Output CGST @ 6%", "Output SGST @ 6%", "Output IGST @ 12%")]
    [InlineData(18, "Output CGST @ 9%", "Output SGST @ 9%", "Output IGST @ 18%")]
    [InlineData(28, "Output CGST @ 14%", "Output SGST @ 14%", "Output IGST @ 28%")]
    [InlineData(0.25, "Output CGST @ 0.125%", "Output SGST @ 0.125%", "Output IGST @ 0.25%")]
    public void Each_tax_has_a_ledger_named_by_its_own_rate(double rate, string cgst, string sgst, string igst)
    {
        var gstRate = (decimal)rate;

        (Ledgers.OutputCgstAt(gstRate), Ledgers.OutputSgstAt(gstRate), Ledgers.OutputIgstAt(gstRate)).ShouldBe((cgst, sgst, igst));
        Ledgers.InputCgstAt(gstRate).ShouldBe(cgst.Replace("Output", "Input", StringComparison.Ordinal));
        Ledgers.InputIgstAt(gstRate).ShouldBe(igst.Replace("Output", "Input", StringComparison.Ordinal));
        Ledgers.OutputCgstAt(12.00m).ShouldBe("Output CGST @ 6%");
    }

    [Fact]
    public void Without_a_rate_or_with_the_setting_off_each_tax_has_one_ledger()
    {
        Ledgers.OutputCgstAt(null).ShouldBe("Output CGST");
        Ledgers.InputIgstAt(0m).ShouldBe("Input IGST");
        var single = Ledgers with { GstLedgerPerRate = false };
        (single.OutputCgstAt(12m), single.InputSgstAt(5m)).ShouldBe(("Output CGST", "Input SGST"));
        (Ledgers with { OutputCgst = "CGST Payable" }).OutputCgstAt(18m).ShouldBe("CGST Payable @ 9%");
    }

    [Fact]
    public void A_bill_posts_its_output_gst_by_the_rate_of_its_lines_and_balances_to_the_paisa()
    {
        var sale = new TallySale("NHOB260000001", Day, null, 2_475.08m, 0m, 0.07m,
        [
            new TallySaleLine("LAB", "Laboratory", 1_000m, 0m, 0m, 0m, GstRate: 0m),
            new TallySaleLine("PHY", "Physiotherapy", 333.33m, 0m, 20m, 20m, GstRate: 12m),
            new TallySaleLine("GS", "General Surgery", 666.67m, 0m, 60.01m, 60.01m, GstRate: 18m),
            new TallySaleLine("GS", "General Surgery", 250m, 0m, 15m, 15m, GstRate: 12m),
            new TallySaleLine(null, null, 33.33m, 0m, 0.83m, 0.83m),
        ], IsCancellation: false);

        var voucher = TallyVoucherBuilder.Build(Book(sales: [sale]), Ledgers).Single();

        ShouldBalance(voucher);
        Entry(voucher, "Output CGST @ 6%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(35m), e => e.IsDebit.ShouldBeFalse());
        Entry(voucher, "Output SGST @ 6%").Amount.ShouldBe(35m);
        Entry(voucher, "Output CGST @ 9%").Amount.ShouldBe(60.01m);
        Entry(voucher, "Output SGST @ 9%").Amount.ShouldBe(60.01m);
        Entry(voucher, "Output CGST").Amount.ShouldBe(0.83m);
        voucher.Entries.Where(e => e.LedgerName.Contains("GST", StringComparison.Ordinal)).Sum(e => e.Amount).ShouldBe(sale.Lines.Sum(l => l.Cgst + l.Sgst + l.Igst));
    }

    [Fact]
    public void A_bill_to_another_state_posts_igst_by_rate_and_its_cancellation_takes_it_back()
    {
        var sale = new TallySale("NHIB260000002", Day, "Star Health", 0m, 1_120m, 0m, [new TallySaleLine("GS", "General Surgery", 1_000m, 0m, 0m, 0m, 120m, 12m)], false);

        var vouchers = TallyVoucherBuilder.Build(Book(sales: [sale, sale with { IsCancellation = true }]), Ledgers);

        Entry(vouchers[0], "Output IGST @ 12%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(120m), e => e.IsDebit.ShouldBeFalse());
        Entry(vouchers[1], "Output IGST @ 12%").IsDebit.ShouldBeTrue();
        vouchers.ToList().ForEach(ShouldBalance);
    }

    [Fact]
    public void A_credit_note_takes_back_gst_by_the_rate_of_the_lines_it_corrects_or_from_the_single_ledgers_without_lines()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(creditNotes:
        [
            new TallyCreditNote("NHCN260000001", Day, CreditNoteKind.CreditNote, 1_180m, Cgst: 45m, Sgst: 45m)
            {
                Gst = [new TallyGstLine(12m, 30m, 30m, 0m), new TallyGstLine(18m, 15m, 15m, 0m)],
            },
            new TallyCreditNote("NHCN260000002", Day, CreditNoteKind.CreditNote, 1_100m, Cgst: 25m, Sgst: 25m),
        ]), Ledgers).ToDictionary(v => v.Number);

        var byRate = vouchers["NHCN260000001"];
        ShouldBalance(byRate);
        Entry(byRate, "Output CGST @ 6%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(30m), e => e.IsDebit.ShouldBeTrue());
        Entry(byRate, "Output SGST @ 9%").Amount.ShouldBe(15m);
        Entry(byRate, "Sales Returns & Allowances").Amount.ShouldBe(1_090m);
        Entry(vouchers["NHCN260000002"], "Output CGST").Amount.ShouldBe(25m);
    }

    [Fact]
    public void Pharmacy_and_canteen_sales_post_their_output_gst_by_rate()
    {
        var pharmacy = new CounterInvoicesDay(TallyCounters.Pharmacy, Day,
            [new CounterInvoice("NHPH260000001", Noon, null, null, null, [new CounterTaxLine(12m, 100m, 6m, 6m), new CounterTaxLine(5m, 42.86m, 1.07m, 1.07m)], 0m,
                [new CounterPayment(CounterPaymentMode.Cash, 157m)])],
            [new CounterReturn("NHPR260000001", Noon, "NHPH260000001", null, null, null, [new CounterTaxLine(12m, 100m, 6m, 6m)], [new CounterPayment(CounterPaymentMode.Cash, 112m)])],
            []);
        var canteen = new CounterSalesDay(TallyCounters.Canteen, Day,
            new CounterDocuments(10, [new CounterTaxLine(5m, 2_000m, 50m, 50m)], 0m, [new CounterMoney(CounterPaymentMode.Cash, 2_100m)]), CounterDocuments.None, []);

        var vouchers = TallyVoucherBuilder.Build(Book() with { CounterInvoices = [pharmacy], Counters = [canteen] }, Ledgers).ToDictionary(v => v.Number);

        Entry(vouchers["NHPH260000001"], "Output CGST @ 6%").Amount.ShouldBe(6m);
        Entry(vouchers["NHPH260000001"], "Output SGST @ 2.5%").Amount.ShouldBe(1.07m);
        Entry(vouchers["NHPR260000001"], "Output CGST @ 6%").IsDebit.ShouldBeTrue();
        Entry(vouchers["CT-20260925"], "Output CGST @ 2.5%").Amount.ShouldBe(50m);
        vouchers.Values.ToList().ForEach(ShouldBalance);
    }

    [Fact]
    public void A_purchase_return_takes_back_input_gst_by_rate_or_from_the_single_ledgers()
    {
        var vendors = new VendorAccountsDay(
            [
                new VendorDebitNoteEntry("NHDN260000001", "Sri Balaji Pharma", null, 300m, 14.5m, 14.5m, 0m, Tax: [new SupplierTaxLine(5m, 100m, 2.5m, 2.5m), new SupplierTaxLine(12m, 200m, 12m, 12m)]),
                new VendorDebitNoteEntry("NHDN260000002", "Sri Balaji Pharma", null, 100m, 0m, 0m, 12m),
            ],
            []);

        var vouchers = TallyVoucherBuilder.Build(Book() with { Vendors = vendors }, Ledgers).ToDictionary(v => v.Number);

        var byRate = vouchers["NHDN260000001"];
        ShouldBalance(byRate);
        Entry(byRate, "Input CGST @ 2.5%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(2.5m), e => e.IsDebit.ShouldBeFalse());
        Entry(byRate, "Input SGST @ 6%").Amount.ShouldBe(12m);
        Entry(vouchers["NHDN260000002"], "Input IGST").Amount.ShouldBe(12m);
    }

    [Fact]
    public void With_the_setting_off_gst_goes_to_one_ledger_per_tax()
    {
        var sale = new TallySale("NHOB260000001", Day, null, 1_120m, 0m, 0m, [new TallySaleLine(null, null, 1_000m, 0m, 60m, 60m, GstRate: 12m)], false);

        var voucher = TallyVoucherBuilder.Build(Book(sales: [sale]), Ledgers with { GstLedgerPerRate = false }).Single();

        Entry(voucher, "Output CGST").Amount.ShouldBe(60m);
        voucher.Entries.ShouldNotContain(e => e.LedgerName.Contains('@'));
    }

    [Fact]
    public void A_b2b_pharmacy_invoice_and_its_return_carry_the_buyers_gstin_state_and_place_of_supply()
    {
        var kerala = new CounterBuyer("32AAACK1234F1Z9", "Kumily Clinic Pvt Ltd", "32");
        var pharmacy = new CounterInvoicesDay(TallyCounters.Pharmacy, Day,
            [
                new CounterInvoice("NHPH260000005", Noon, null, "Kumily Clinic", kerala, [new CounterTaxLine(12m, 100m, 0m, 0m, 12m)], 0m, []),
                new CounterInvoice("NHPH260000006", Noon, null, "Kumily Clinic", kerala, [new CounterTaxLine(12m, 100m, 0m, 0m, 12m)], 0m,
                    [new CounterPayment(CounterPaymentMode.Upi, 100m, "UPI1"), new CounterPayment(CounterPaymentMode.Cash, 12m)]),
                new CounterInvoice("NHPH260000007", Noon, null, "Ramesh", null, [new CounterTaxLine(12m, 100m, 6m, 6m)], 0m, [new CounterPayment(CounterPaymentMode.Cash, 112m)]),
            ],
            [new CounterReturn("NHPR260000002", Noon, "NHPH260000005", null, "Kumily Clinic", kerala, [new CounterTaxLine(12m, 50m, 0m, 0m, 6m)], [])],
            []);

        var vouchers = TallyVoucherBuilder.Build(Book() with { CounterInvoices = [pharmacy] }, Ledgers).ToDictionary(v => v.Number);

        vouchers["NHPH260000005"].Party.ShouldBe(new AccountingParty("Pharmacy Receivables", "32AAACK1234F1Z9", "Kerala", "Kerala"));
        vouchers["NHPH260000006"].Party.ShouldBe(new AccountingParty("UPI Settlement Account", "32AAACK1234F1Z9", "Kerala", "Kerala"));
        vouchers["NHPH260000007"].Party.ShouldBeNull();
        vouchers["NHPR260000002"].Party.ShouldBe(new AccountingParty("Pharmacy Receivables", "32AAACK1234F1Z9", "Kerala", "Kerala"));
        Entry(vouchers["NHPH260000005"], "Output IGST @ 12%").Amount.ShouldBe(12m);
        vouchers.Values.ShouldAllBe(v => v.SupplierInvoiceNumber == null);
    }

    [Fact]
    public void A_state_is_named_from_its_gst_code_or_a_gstin()
    {
        TallyGst.StateName("33").ShouldBe("Tamil Nadu");
        TallyGst.StateName("32AAACK1234F1Z9").ShouldBe("Kerala");
        TallyGst.StateName("99").ShouldBeNull();
        TallyGst.StateName(" ").ShouldBeNull();
        TallyGst.Party("Sri Balaji Pharma", " 33aabcs1234k1z5 ", null, "33").ShouldBe(new AccountingParty("Sri Balaji Pharma", "33AABCS1234K1Z5", "Tamil Nadu", "Tamil Nadu"));
        TallyGst.Party("Sundry Creditors", null, "29", "33").ShouldBe(new AccountingParty("Sundry Creditors", null, "Karnataka", "Tamil Nadu"));
    }

    [Fact]
    public void The_party_is_part_of_what_was_sent()
    {
        var voucher = new AccountingVoucher("Pharmacy Sales", Day, "NHPH260000005", "Pharmacy invoice NHPH260000005",
            [new AccountingEntry("Pharmacy Receivables", 112m, true), new AccountingEntry("Pharmacy Sales @ 12%", 112m, false)], "NHPH260000005");
        var withParty = voucher with { Party = new AccountingParty("Pharmacy Receivables", "32AAACK1234F1Z9", "Kerala", "Kerala") };

        TallyTracking.Fingerprint(withParty).ShouldNotBe(TallyTracking.Fingerprint(voucher));
        TallyTracking.Fingerprint(withParty with { Party = withParty.Party! with { Gstin = "32AAACK1234F1Z8" } }).ShouldNotBe(TallyTracking.Fingerprint(withParty));
        TallyTracking.Fingerprint(voucher with { SupplierInvoiceNumber = "SB/2291" }).ShouldNotBe(TallyTracking.Fingerprint(voucher));
    }

    [Fact]
    public void Gst_by_rate_is_the_default_and_comes_from_the_settings() =>
        JsonSerializer.Deserialize<TallyLedgerMap>("""{"gstLedgerPerRate":false}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .ShouldSatisfyAllConditions(m => m.GstLedgerPerRate.ShouldBeFalse(), _ => new TallyLedgerMap().GstLedgerPerRate.ShouldBeTrue());
}
