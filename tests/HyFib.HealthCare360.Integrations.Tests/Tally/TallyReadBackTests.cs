using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Tally;

public sealed class TallyReadBackTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly TallyImportOptions Options = new(null, "Primary Cost Category", new Dictionary<string, string> { ["Cash"] = "Cash-in-Hand" });

    private static AccountingVoucher Receipt(string number, decimal amount = 500m) => new(
        "Receipt", Day, number, $"Receipt {number}",
        [new AccountingEntry("Cash", amount, true), new AccountingEntry("Patient Receivables", amount, false)], number);

    private static TallyVoucherCopy Copy(string remoteId, decimal amount = 500m, string type = "Receipt", bool inBooks = true, string cashLedger = "Cash-in-Hand") =>
        new(remoteId, type, "7", Day, [new TallyLedgerLine(cashLedger, amount, true), new TallyLedgerLine("Patient Receivables", amount, false)], inBooks);

    private static AccountingVoucherCheck Check(AccountingVoucher sent, params TallyVoucherCopy[] found) =>
        TallyReadBack.Compare(Day, [sent], found, Options).Vouchers.ShouldHaveSingleItem();

    [Fact]
    public void A_voucher_tally_holds_as_sent_matches_after_the_ledger_map_and_reports_tallys_number()
    {
        var check = Check(Receipt("RC1"), Copy("HC360-RC1"));

        check.ShouldBe(new AccountingVoucherCheck("RC1", AccountingVoucherMatch.Matched, 500m, "7", null));
    }

    [Fact]
    public void Ledger_names_are_compared_without_case_and_as_net_amounts()
    {
        var copy = new TallyVoucherCopy("HC360-RC1", "receipt", "RC1", Day,
            [new TallyLedgerLine("PATIENT RECEIVABLES", 300m, false), new TallyLedgerLine("cash-in-hand", 500m, true), new TallyLedgerLine("Patient Receivables", 200m, false)], true);

        Check(Receipt("RC1"), copy).Match.ShouldBe(AccountingVoucherMatch.Matched);
    }

    [Fact]
    public void A_voucher_tally_does_not_hold_is_missing() =>
        Check(Receipt("RC1"), Copy("HC360-RC2")).Match.ShouldBe(AccountingVoucherMatch.Missing);

    [Fact]
    public void A_voucher_with_another_amount_in_tally_says_both_amounts()
    {
        var check = Check(Receipt("RC1"), Copy("HC360-RC1", amount: 450m));

        check.Match.ShouldBe(AccountingVoucherMatch.AmountDiffers);
        check.FoundAmount.ShouldBe(450m);
        check.Detail.ShouldBe("Tally holds ₹450.00; HC360 sent ₹500.00.");
    }

    [Fact]
    public void A_voucher_posted_to_another_ledger_in_tally_has_changed()
    {
        var check = Check(Receipt("RC1"), Copy("HC360-RC1", cashLedger: "Petty Cash"));

        check.Match.ShouldBe(AccountingVoucherMatch.Changed);
        check.Detail.ShouldBe("Cash-in-Hand: HC360 ₹500.00 Dr, Tally nothing; Petty Cash: HC360 nothing, Tally ₹500.00 Dr.");
    }

    [Fact]
    public void A_voucher_of_another_type_or_date_has_changed()
    {
        var check = Check(Receipt("RC1"), Copy("HC360-RC1", type: "Journal") with { Date = Day.AddDays(1) });

        check.Match.ShouldBe(AccountingVoucherMatch.Changed);
        check.Detail.ShouldBe("It is a Journal voucher in Tally; Tally dates it 26-Sep-2026.");
    }

    [Fact]
    public void A_voucher_cancelled_in_tally_is_not_in_the_books() =>
        Check(Receipt("RC1"), Copy("HC360-RC1", inBooks: false)).Match.ShouldBe(AccountingVoucherMatch.NotInBooks);

    [Fact]
    public void Remote_ids_are_matched_without_case() =>
        Check(Receipt("NHRC260000001"), Copy("hc360-nhrc260000001")).Match.ShouldBe(AccountingVoucherMatch.Matched);

    [Fact]
    public void Other_hc360_vouchers_on_the_day_are_listed_and_the_hospitals_own_are_ignored()
    {
        var found = TallyVoucherReader.Read(TallyFixtures.DayExport).Vouchers;
        var advance = new AccountingVoucher("Receipt", Day, "RCPT-1", "Advance received",
            [new AccountingEntry("Cash", 500m, true), new AccountingEntry("Patient Advances", 500m, false)], "RCPT-1");

        var result = TallyReadBack.Compare(Day, [advance], found, new TallyImportOptions(null, "Primary Cost Category", null));

        result.Success.ShouldBeTrue();
        result.Vouchers.ShouldHaveSingleItem().Match.ShouldBe(AccountingVoucherMatch.Matched);
        result.Others.ShouldBe([
            new AccountingVoucherFound("DF-20260925-001", "Journal", "DF-20260925-001", Day, 1200m, InBooks: false),
            new AccountingVoucherFound("NHOB260000001", "Sales", "17", Day, 1180m, InBooks: true),
        ]);
        result.Message.ShouldBe("Tally holds 1 of the 1 voucher(s); 0 differ; 2 other HC360 voucher(s) on the day.");
    }

    [Fact]
    public void A_voucher_without_a_reference_cannot_be_found()
    {
        var check = Check(Receipt("RC1") with { Reference = null }, Copy("HC360-RC1"));

        check.Key.ShouldBe("RC1");
        check.Match.ShouldBe(AccountingVoucherMatch.Missing);
    }

    [Fact]
    public void What_the_simulator_keeps_reads_back_as_sent()
    {
        var sent = new AccountingVoucher("Sales", Day, "NHOB260000001", "Bill NHOB260000001",
        [
            new AccountingEntry("Patient Receivables", 1180m, true),
            new AccountingEntry("Laboratory Income", 1000m, false, "Laboratory"),
            new AccountingEntry("Output CGST", 90m, false),
            new AccountingEntry("Output SGST", 90m, false),
        ], "NHOB260000001");
        var xml = "<ENVELOPE><BODY><DATA><COLLECTION>" + TallyXml.Voucher(sent, Options) + "</COLLECTION></DATA></BODY></ENVELOPE>";

        var result = TallyReadBack.Compare(Day, [sent], TallyVoucherReader.Read(xml).Vouchers, Options);

        result.Vouchers.ShouldHaveSingleItem().Match.ShouldBe(AccountingVoucherMatch.Matched);
        result.Others.ShouldBeEmpty();
    }
}
