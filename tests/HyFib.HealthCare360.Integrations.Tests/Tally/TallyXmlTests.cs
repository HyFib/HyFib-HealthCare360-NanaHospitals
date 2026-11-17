using System.Globalization;
using System.Xml.Linq;
using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Tally;

public sealed class TallyXmlTests
{
    private static readonly TallyImportOptions Options = new("Nana Hospitals Pvt Ltd", "Primary Cost Category",
        new Dictionary<string, string> { ["Cash"] = "Cash-in-Hand (Main Counter)" });

    private static AccountingVoucher Sales(string? reference = "BILL-42") => new(
        "Sales",
        new DateOnly(2026, 9, 25),
        "BILL-42",
        "OPD bill for Ravi & Sons <walk-in>",
        [
            new AccountingEntry("Cash", 1180m, IsDebit: true),
            new AccountingEntry("OPD Income", 1000m, IsDebit: false, CostCentre: "Cardiology"),
            new AccountingEntry("Output CGST 9%", 90m, IsDebit: false),
            new AccountingEntry("Output SGST 9%", 90m, IsDebit: false),
        ],
        reference);

    [Fact]
    public void Builds_an_import_data_envelope_for_the_configured_company()
    {
        var document = XDocument.Parse(TallyXml.BuildImport([Sales()], Options));

        var root = document.Root!;
        root.Name.LocalName.ShouldBe("ENVELOPE");
        root.Element("HEADER")!.Element("TALLYREQUEST")!.Value.ShouldBe("Import Data");
        var description = root.Element("BODY")!.Element("IMPORTDATA")!.Element("REQUESTDESC")!;
        description.Element("REPORTNAME")!.Value.ShouldBe("Vouchers");
        description.Element("STATICVARIABLES")!.Element("SVCURRENTCOMPANY")!.Value.ShouldBe("Nana Hospitals Pvt Ltd");
        var message = root.Element("BODY")!.Element("IMPORTDATA")!.Element("REQUESTDATA")!.Element("TALLYMESSAGE")!;
        message.Element("VOUCHER").ShouldNotBeNull();
    }

    [Fact]
    public void Writes_the_voucher_header_tally_expects()
    {
        var xml = TallyXml.BuildImport([Sales()], Options);

        xml.ShouldContain("""<TALLYMESSAGE xmlns:UDF="TallyUDF">""");
        xml.ShouldContain("""<VOUCHER REMOTEID="HC360-BILL-42" VCHTYPE="Sales" ACTION="Create" OBJVIEW="Accounting Voucher View">""");
        var voucher = XDocument.Parse(xml).Descendants("VOUCHER").ShouldHaveSingleItem();
        voucher.Element("DATE")!.Value.ShouldBe("20260925");
        voucher.Element("EFFECTIVEDATE")!.Value.ShouldBe("20260925");
        voucher.Element("VOUCHERTYPENAME")!.Value.ShouldBe("Sales");
        voucher.Element("VOUCHERNUMBER")!.Value.ShouldBe("BILL-42");
        voucher.Element("PERSISTEDVIEW")!.Value.ShouldBe("Accounting Voucher View");
        voucher.Element("NARRATION")!.Value.ShouldBe("OPD bill for Ravi & Sons <walk-in>");
    }

    [Fact]
    public void Debits_are_deemed_positive_with_negative_amounts_and_credits_the_opposite()
    {
        var entries = XDocument.Parse(TallyXml.BuildImport([Sales()], Options)).Descendants("ALLLEDGERENTRIES.LIST").ToList();

        entries.Count.ShouldBe(4);
        entries[0].Element("LEDGERNAME")!.Value.ShouldBe("Cash-in-Hand (Main Counter)");
        entries[0].Element("ISDEEMEDPOSITIVE")!.Value.ShouldBe("Yes");
        entries[0].Element("AMOUNT")!.Value.ShouldBe("-1180.00");
        entries[1].Element("LEDGERNAME")!.Value.ShouldBe("OPD Income");
        entries[1].Element("ISDEEMEDPOSITIVE")!.Value.ShouldBe("No");
        entries[1].Element("AMOUNT")!.Value.ShouldBe("1000.00");
        entries[3].Element("AMOUNT")!.Value.ShouldBe("90.00");
    }

    [Fact]
    public void Allocates_cost_centres_under_the_configured_category()
    {
        var entries = XDocument.Parse(TallyXml.BuildImport([Sales()], Options)).Descendants("ALLLEDGERENTRIES.LIST").ToList();

        var allocation = entries[1].Element("CATEGORYALLOCATIONS.LIST")!;
        allocation.Element("CATEGORY")!.Value.ShouldBe("Primary Cost Category");
        var centre = allocation.Element("COSTCENTREALLOCATIONS.LIST")!;
        centre.Element("NAME")!.Value.ShouldBe("Cardiology");
        centre.Element("AMOUNT")!.Value.ShouldBe("1000.00");
        entries[0].Element("CATEGORYALLOCATIONS.LIST").ShouldBeNull();
    }

    [Fact]
    public void Escapes_markup_and_drops_control_characters()
    {
        var voucher = Sales() with { Narration = "Refund\u0001 for <b>test</b> & review" };

        var xml = TallyXml.BuildImport([voucher], Options);

        xml.ShouldContain("Refund for &lt;b&gt;test&lt;/b&gt; &amp; review");
        xml.ShouldNotContain("\u0001");
        xml.ShouldNotContain("<b>");
    }

    [Fact]
    public void Omits_the_company_and_remote_id_when_not_known()
    {
        var document = XDocument.Parse(TallyXml.BuildImport([Sales(reference: null)], Options with { CompanyName = " " }));

        document.Descendants("STATICVARIABLES").ShouldHaveSingleItem().HasElements.ShouldBeFalse();
        document.Descendants("VOUCHER").ShouldHaveSingleItem().Attribute("REMOTEID").ShouldBeNull();
    }

    [Fact]
    public void Puts_every_voucher_in_its_own_tally_message()
    {
        var second = Sales("BILL-43") with { Number = "BILL-43" };

        var messages = XDocument.Parse(TallyXml.BuildImport([Sales(), second], Options)).Descendants("TALLYMESSAGE").ToList();

        messages.Count.ShouldBe(2);
        messages[1].Element("VOUCHER")!.Attribute("REMOTEID")!.Value.ShouldBe("HC360-BILL-43");
    }

    [Theory]
    [InlineData("1180", true, "-1180.00")]
    [InlineData("10.005", true, "-10.01")]
    [InlineData("99.994", false, "99.99")]
    [InlineData("0.5", false, "0.50")]
    public void Formats_amounts_with_two_decimals_in_the_invariant_culture(string amount, bool isDebit, string expected) =>
        TallyXml.Amount(new AccountingEntry("Cash", decimal.Parse(amount, CultureInfo.InvariantCulture), isDebit)).ShouldBe(expected);

    [Fact]
    public void Remote_ids_are_prefixed_and_trimmed() => TallyXml.RemoteId(" BILL-42 ").ShouldBe("HC360-BILL-42");

    [Fact]
    public void Maps_ledgers_and_keeps_unmapped_names()
    {
        Options.MapLedger("Cash").ShouldBe("Cash-in-Hand (Main Counter)");
        Options.MapLedger(" Pharmacy Sales ").ShouldBe("Pharmacy Sales");
        (Options with { LedgerMap = new Dictionary<string, string> { ["Cash"] = " " } }).MapLedger("Cash").ShouldBe("Cash");
    }

    [Fact]
    public void A_balanced_voucher_is_valid() => TallyXml.Validate(Sales()).ShouldBeNull();

    [Fact]
    public void Reports_unbalanced_vouchers_with_both_totals() =>
        TallyXml.Validate(Sales() with { Entries = [new AccountingEntry("Cash", 1180m, true), new AccountingEntry("OPD Income", 1000m, false)] })
            .ShouldBe("debits (1180.00) and credits (1000.00) do not balance");

    [Fact]
    public void Rejects_vouchers_tally_would_refuse()
    {
        TallyXml.Validate(Sales() with { Number = " " }).ShouldBe("voucher type and number are required");
        TallyXml.Validate(Sales() with { Entries = [new AccountingEntry("Cash", 10m, true)] }).ShouldBe("a voucher needs at least two ledger entries");
        TallyXml.Validate(Sales() with { Entries = [new AccountingEntry("", 10m, true), new AccountingEntry("Income", 10m, false)] })
            .ShouldBe("every entry needs a ledger");
        TallyXml.Validate(Sales() with { Entries = [new AccountingEntry("Cash", -10m, true), new AccountingEntry("Income", -10m, false)] })
            .ShouldBe("amounts must be positive (IsDebit decides the side)");
    }

    /// <summary>An insurer's settlement credited against the bill it settles, and a sale's payer share as a new bill.</summary>
    private static AccountingVoucher WithBills() => new(
        "Receipt",
        new DateOnly(2026, 9, 25),
        "NHRC260000001",
        "Receipt NHRC260000001",
        [
            new AccountingEntry("Bank", 1_500m, IsDebit: true),
            new AccountingEntry("Star Health", 1_000m, IsDebit: false)
            {
                BillAllocations = [new AccountingBillAllocation("NHOB<26>&0001", AccountingBillType.AgainstRef, 1_000m)],
            },
            new AccountingEntry("Sri Balaji Pharma", 500m, IsDebit: false)
            {
                BillAllocations =
                [
                    new AccountingBillAllocation("SB/2291", AccountingBillType.NewRef, 300m),
                    new AccountingBillAllocation(" ", AccountingBillType.OnAccount, 200m),
                ],
            },
        ],
        "NHRC260000001");

    [Fact]
    public void Writes_bill_allocations_inside_the_ledger_line_with_the_lines_sign()
    {
        var lines = XDocument.Parse(TallyXml.BuildImport([WithBills()], Options)).Descendants("ALLLEDGERENTRIES.LIST").ToList();

        lines[0].Elements("BILLALLOCATIONS.LIST").ShouldBeEmpty();
        var against = lines[1].Elements("BILLALLOCATIONS.LIST").ShouldHaveSingleItem();
        against.Element("NAME")!.Value.ShouldBe("NHOB<26>&0001");
        against.Element("BILLTYPE")!.Value.ShouldBe("Agst Ref");
        against.Element("AMOUNT")!.Value.ShouldBe("1000.00");
        var supplier = lines[2].Elements("BILLALLOCATIONS.LIST").ToList();
        supplier.Select(b => (b.Element("NAME")?.Value, b.Element("BILLTYPE")!.Value, b.Element("AMOUNT")!.Value))
            .ShouldBe([("SB/2291", "New Ref", "300.00"), (null, "On Account", "200.00")]);
    }

    [Fact]
    public void A_debit_lines_bill_allocations_are_negative_like_the_line()
    {
        var voucher = WithBills() with
        {
            Entries =
            [
                new AccountingEntry("Star Health", 1_000m, IsDebit: true) { BillAllocations = [new("NHOB260000001", AccountingBillType.NewRef, 1_000m)] },
                new AccountingEntry("Hospital Service Income", 1_000m, IsDebit: false),
            ],
        };

        var line = XDocument.Parse(TallyXml.BuildImport([voucher], Options)).Descendants("ALLLEDGERENTRIES.LIST").First();

        line.Element("AMOUNT")!.Value.ShouldBe("-1000.00");
        line.Element("BILLALLOCATIONS.LIST")!.Element("AMOUNT")!.Value.ShouldBe("-1000.00");
        line.Element("BILLALLOCATIONS.LIST")!.Element("BILLTYPE")!.Value.ShouldBe("New Ref");
    }

    [Fact]
    public void Bill_names_are_escaped_like_every_other_value()
    {
        var xml = TallyXml.BuildImport([WithBills()], Options);

        xml.ShouldContain("<NAME>NHOB&lt;26&gt;&amp;0001</NAME>");
        xml.ShouldNotContain("NHOB<26>");
    }

    [Theory]
    [InlineData(AccountingBillType.NewRef, "New Ref")]
    [InlineData(AccountingBillType.AgainstRef, "Agst Ref")]
    [InlineData(AccountingBillType.Advance, "Advance")]
    [InlineData(AccountingBillType.OnAccount, "On Account")]
    public void Names_each_kind_of_bill_reference_as_tally_does(AccountingBillType type, string name) => TallyXml.BillType(type).ShouldBe(name);

    [Fact]
    public void A_voucher_whose_bill_allocations_add_up_is_valid() => TallyXml.Validate(WithBills()).ShouldBeNull();

    [Fact]
    public void Refuses_bill_allocations_that_do_not_add_up_to_their_line()
    {
        var voucher = WithBills();
        var shortOfIt = voucher with
        {
            Entries = [voucher.Entries[0], voucher.Entries[1] with { BillAllocations = [new("NHOB260000001", AccountingBillType.AgainstRef, 999.99m)] }, voucher.Entries[2]],
        };

        TallyXml.Validate(shortOfIt).ShouldBe("the bill allocations of Star Health (999.99) do not add up to its amount (1000.00)");
    }

    [Fact]
    public void Refuses_bill_allocations_that_are_not_positive_or_name_no_bill()
    {
        var voucher = WithBills();
        AccountingVoucher With(params AccountingBillAllocation[] bills) =>
            voucher with { Entries = [voucher.Entries[0], voucher.Entries[1] with { BillAllocations = bills }, voucher.Entries[2]] };

        TallyXml.Validate(With(
                new AccountingBillAllocation("NHOB260000001", AccountingBillType.AgainstRef, 1_200m),
                new AccountingBillAllocation("NHOB260000002", AccountingBillType.AgainstRef, -200m)))
            .ShouldBe("the bill allocations of Star Health must be positive (the line decides the side)");
        TallyXml.Validate(With(new AccountingBillAllocation(" ", AccountingBillType.NewRef, 1_000m)))
            .ShouldBe("every bill allocation of Star Health except On Account needs the bill's name");
        TallyXml.Validate(With(new AccountingBillAllocation(null, AccountingBillType.OnAccount, 1_000m))).ShouldBeNull();
    }

    [Fact]
    public void A_voucher_with_bill_allocations_reads_back_with_its_lines_amounts_and_matches()
    {
        var voucher = WithBills();
        var copy = TallyVoucherReader.Parse(TallyXml.Voucher(voucher, Options));

        copy.Lines.Select(l => (l.Ledger, l.Amount, l.IsDebit)).ShouldBe([("Bank", 1_500m, true), ("Star Health", 1_000m, false), ("Sri Balaji Pharma", 500m, false)]);
        var check = TallyReadBack.Compare(voucher.Date, [voucher], [copy], Options).Vouchers.ShouldHaveSingleItem();
        check.Match.ShouldBe(AccountingVoucherMatch.Matched);
    }

    /// <summary>A receipt of a cheque and a transfer into the bank, and a refund by transfer out of it.</summary>
    private static AccountingVoucher WithBank() => new(
        "Receipt",
        new DateOnly(2026, 9, 25),
        "NHRC260000003",
        "Receipt NHRC260000003",
        [
            new AccountingEntry("Bank", 5_000m, IsDebit: true)
            {
                BankAllocations =
                [
                    new AccountingBankAllocation(new DateOnly(2026, 9, 25), AccountingBankTransaction.Cheque, 2_000m, "123456", new DateOnly(2026, 9, 20), "State Bank <of> India & Co"),
                    new AccountingBankAllocation(new DateOnly(2026, 9, 25), AccountingBankTransaction.EFundTransfer, 3_000m, "HDFCR52026092512345"),
                ],
            },
            new AccountingEntry("Patient Receivables", 5_000m, IsDebit: false),
        ],
        "NHRC260000003");

    [Fact]
    public void Writes_bank_allocations_with_the_instrument_its_date_and_the_lines_sign()
    {
        var line = XDocument.Parse(TallyXml.BuildImport([WithBank()], Options)).Descendants("ALLLEDGERENTRIES.LIST").First();

        var parts = line.Elements("BANKALLOCATIONS.LIST").ToList();
        parts.Count.ShouldBe(2);
        parts[0].Element("DATE")!.Value.ShouldBe("20260925");
        parts[0].Element("INSTRUMENTDATE")!.Value.ShouldBe("20260920");
        parts[0].Element("TRANSACTIONTYPE")!.Value.ShouldBe("Cheque");
        parts[0].Element("INSTRUMENTNUMBER")!.Value.ShouldBe("123456");
        parts[0].Element("BANKNAME")!.Value.ShouldBe("State Bank <of> India & Co");
        parts[0].Element("AMOUNT")!.Value.ShouldBe("-2000.00");
        parts[1].Element("INSTRUMENTDATE")!.Value.ShouldBe("20260925");
        parts[1].Element("TRANSACTIONTYPE")!.Value.ShouldBe("e-Fund Transfer");
        parts[1].Element("INSTRUMENTNUMBER")!.Value.ShouldBe("HDFCR52026092512345");
        parts[1].Element("BANKNAME").ShouldBeNull();
        parts[1].Element("AMOUNT")!.Value.ShouldBe("-3000.00");
        XDocument.Parse(TallyXml.BuildImport([WithBank()], Options)).Descendants("ALLLEDGERENTRIES.LIST").Last().Elements("BANKALLOCATIONS.LIST").ShouldBeEmpty();
    }

    [Fact]
    public void A_credit_lines_bank_allocation_is_positive_and_an_unknown_instrument_is_left_out()
    {
        var refund = WithBank() with
        {
            Entries =
            [
                new AccountingEntry("Patient Advances", 700m, IsDebit: true),
                new AccountingEntry("Bank", 700m, IsDebit: false) { BankAllocations = [new(new DateOnly(2026, 9, 25), AccountingBankTransaction.Others, 700m)] },
            ],
        };

        var part = XDocument.Parse(TallyXml.BuildImport([refund], Options)).Descendants("BANKALLOCATIONS.LIST").ShouldHaveSingleItem();

        part.Element("TRANSACTIONTYPE")!.Value.ShouldBe("Others");
        part.Element("INSTRUMENTNUMBER").ShouldBeNull();
        part.Element("AMOUNT")!.Value.ShouldBe("700.00");
    }

    [Fact]
    public void Bank_names_and_instruments_are_escaped() =>
        TallyXml.BuildImport([WithBank()], Options).ShouldContain("<BANKNAME>State Bank &lt;of&gt; India &amp; Co</BANKNAME>");

    [Theory]
    [InlineData(AccountingBankTransaction.Cheque, "Cheque")]
    [InlineData(AccountingBankTransaction.EFundTransfer, "e-Fund Transfer")]
    [InlineData(AccountingBankTransaction.Others, "Others")]
    public void Names_each_kind_of_bank_transaction_as_tally_does(AccountingBankTransaction type, string name) => TallyXml.TransactionType(type).ShouldBe(name);

    [Fact]
    public void A_voucher_whose_bank_allocations_add_up_is_valid() => TallyXml.Validate(WithBank()).ShouldBeNull();

    [Fact]
    public void Refuses_bank_allocations_that_do_not_add_up_or_are_not_positive()
    {
        var voucher = WithBank();
        AccountingVoucher With(params AccountingBankAllocation[] parts) => voucher with { Entries = [voucher.Entries[0] with { BankAllocations = parts }, voucher.Entries[1]] };

        TallyXml.Validate(With(new AccountingBankAllocation(voucher.Date, AccountingBankTransaction.Cheque, 4_000m, "123456")))
            .ShouldBe("the bank allocations of Bank (4000.00) do not add up to its amount (5000.00)");
        TallyXml.Validate(With(
                new AccountingBankAllocation(voucher.Date, AccountingBankTransaction.Cheque, 6_000m, "123456"),
                new AccountingBankAllocation(voucher.Date, AccountingBankTransaction.Others, -1_000m)))
            .ShouldBe("the bank allocations of Bank must be positive (the line decides the side)");
    }

    [Fact]
    public void A_voucher_with_bank_allocations_reads_back_with_its_lines_amounts_and_matches()
    {
        var voucher = WithBank();
        var copy = TallyVoucherReader.Parse(TallyXml.Voucher(voucher, Options));

        copy.Lines.Select(l => (l.Ledger, l.Amount, l.IsDebit)).ShouldBe([("Bank", 5_000m, true), ("Patient Receivables", 5_000m, false)]);
        TallyReadBack.Compare(voucher.Date, [voucher], [copy], Options).Vouchers.ShouldHaveSingleItem().Match.ShouldBe(AccountingVoucherMatch.Matched);
    }

    [Fact]
    public void Builds_the_company_list_health_check()
    {
        var root = XDocument.Parse(TallyXml.BuildCompanyListRequest()).Root!;

        var header = root.Element("HEADER")!;
        header.Element("TALLYREQUEST")!.Value.ShouldBe("Export");
        header.Element("TYPE")!.Value.ShouldBe("Collection");
        header.Element("ID")!.Value.ShouldBe("HC360Companies");
        var collection = root.Descendants("COLLECTION").ShouldHaveSingleItem();
        collection.Attribute("NAME")!.Value.ShouldBe("HC360Companies");
        collection.Element("TYPE")!.Value.ShouldBe("Company");
        root.Descendants("SVEXPORTFORMAT").ShouldHaveSingleItem().Value.ShouldBe("$$SysName:XML");
    }

    [Fact]
    public void Reads_open_company_names_from_the_collection_export()
    {
        var document = XDocument.Parse("""
            <ENVELOPE>
              <HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER>
              <BODY><DESC></DESC><DATA><COLLECTION>
                <COMPANY NAME="Nana Hospitals Pvt Ltd" RESERVEDNAME=""><NAME TYPE="String">Nana Hospitals Pvt Ltd</NAME></COMPANY>
                <COMPANY><NAME>Demo Company</NAME></COMPANY>
                <COMPANY NAME="nana hospitals pvt ltd" />
              </COLLECTION></DATA></BODY>
            </ENVELOPE>
            """);

        TallyXml.ParseCompanyNames(document).ShouldBe(["Nana Hospitals Pvt Ltd", "Demo Company"]);
    }
}
