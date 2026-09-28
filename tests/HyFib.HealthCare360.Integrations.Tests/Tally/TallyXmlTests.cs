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
