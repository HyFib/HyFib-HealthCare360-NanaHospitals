using System.Text;
using System.Xml;
using System.Xml.Linq;
using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Tally;

public sealed class TallyVoucherReaderTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);

    [Fact]
    public void Reads_vouchers_from_a_collection_export()
    {
        var read = TallyVoucherReader.Read(TallyFixtures.DayExport);

        read.Error.ShouldBeNull();
        read.Vouchers.Count.ShouldBe(4);
        var receipt = read.Vouchers[0];
        receipt.RemoteId.ShouldBe("HC360-RCPT-1");
        receipt.VoucherType.ShouldBe("Receipt");
        receipt.Number.ShouldBe("RCPT-1");
        receipt.Date.ShouldBe(Day);
        receipt.InBooks.ShouldBeTrue();
        receipt.Lines.ShouldBe([new TallyLedgerLine("Cash", 500m, true), new TallyLedgerLine("Patient Advances", 500m, false)]);
        receipt.Amount.ShouldBe(500m);
    }

    [Fact]
    public void Takes_the_remote_id_from_the_guid_when_the_attribute_is_missing() =>
        TallyVoucherReader.Read(TallyFixtures.DayExport).Vouchers[3].RemoteId.ShouldBe("HC360-DF-20260925-001");

    [Fact]
    public void Reads_ledger_entries_of_vouchers_in_invoice_view()
    {
        var sale = TallyVoucherReader.Read(TallyFixtures.DayExport).Vouchers[2];

        sale.Lines.Count.ShouldBe(4);
        sale.Amount.ShouldBe(1180m);
        sale.Number.ShouldBe("17");
    }

    [Fact]
    public void A_cancelled_voucher_is_not_in_the_books() =>
        TallyVoucherReader.Read(TallyFixtures.DayExport).Vouchers[3].InBooks.ShouldBeFalse();

    [Fact]
    public void Reads_the_vouchers_of_an_import_envelope_as_they_were_sent()
    {
        var voucher = new AccountingVoucher("Journal", Day, "DF-NH-20260925-DR005", "Doctor fees accrued",
            [new AccountingEntry("Doctor Professional Fees", 1200m, true), new AccountingEntry("Doctor Fees Payable - Dr. Arjun Mehta", 1200m, false)],
            "DF-NH-20260925-DR005");

        var copy = TallyVoucherReader.Read(TallyXml.BuildImport([voucher], new TallyImportOptions(null, "Primary Cost Category", null))).Vouchers.ShouldHaveSingleItem();

        copy.RemoteId.ShouldBe("HC360-DF-NH-20260925-DR005");
        copy.VoucherType.ShouldBe("Journal");
        copy.Date.ShouldBe(Day);
        copy.Amount.ShouldBe(1200m);
    }

    [Theory]
    [InlineData("20260925")]
    [InlineData("25-Sep-2026")]
    [InlineData("2026-09-25")]
    public void Reads_the_date_formats_tally_writes(string date) =>
        TallyVoucherReader.Parse(XElement.Parse($"<VOUCHER><DATE>{date}</DATE></VOUCHER>")).Date.ShouldBe(Day);

    [Fact]
    public void Takes_the_side_from_the_sign_when_deemed_positive_is_missing()
    {
        var voucher = TallyVoucherReader.Parse(XElement.Parse("""
            <VOUCHER>
              <ALLLEDGERENTRIES.LIST><LEDGERNAME>Cash</LEDGERNAME><AMOUNT>-1,180.00</AMOUNT></ALLLEDGERENTRIES.LIST>
              <ALLLEDGERENTRIES.LIST><LEDGERNAME>Sales</LEDGERNAME><AMOUNT>1180</AMOUNT></ALLLEDGERENTRIES.LIST>
              <ALLLEDGERENTRIES.LIST><LEDGERNAME>Empty</LEDGERNAME><AMOUNT>0</AMOUNT></ALLLEDGERENTRIES.LIST>
              <ALLLEDGERENTRIES.LIST><LEDGERNAME></LEDGERNAME><AMOUNT>5</AMOUNT></ALLLEDGERENTRIES.LIST>
            </VOUCHER>
            """));

        voucher.Lines.ShouldBe([new TallyLedgerLine("Cash", 1180m, true), new TallyLedgerLine("Sales", 1180m, false)]);
    }

    [Fact]
    public void A_failed_status_is_reported_with_its_reason()
    {
        var read = TallyVoucherReader.Read("<ENVELOPE><HEADER><VERSION>1</VERSION><STATUS>0</STATUS></HEADER><BODY><DATA>Company not loaded</DATA></BODY></ENVELOPE>");

        read.Vouchers.ShouldBeEmpty();
        read.Error.ShouldBe("Company not loaded");
    }

    [Fact]
    public void A_line_error_is_reported_even_when_vouchers_came_with_it()
    {
        var read = TallyVoucherReader.Read("<ENVELOPE><BODY><DATA><LINEERROR>Period is not set</LINEERROR><COLLECTION><VOUCHER /></COLLECTION></DATA></BODY></ENVELOPE>");

        read.Vouchers.ShouldBeEmpty();
        read.Error.ShouldBe("Period is not set");
    }

    [Fact]
    public void Tolerates_the_control_character_references_tally_emits() =>
        TallyVoucherReader.Read("<ENVELOPE><COLLECTION><VOUCHER><VOUCHERNUMBER>A&#4;1</VOUCHERNUMBER></VOUCHER></COLLECTION></ENVELOPE>")
            .Vouchers.ShouldHaveSingleItem().Number.ShouldBe("A\u00041");

    [Fact]
    public void Refuses_document_type_definitions() =>
        Should.Throw<XmlException>(() => TallyVoucherReader.Read("""
            <?xml version="1.0"?>
            <!DOCTYPE ENVELOPE [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <ENVELOPE><VOUCHER><VOUCHERNUMBER>&xxe;</VOUCHERNUMBER></VOUCHER></ENVELOPE>
            """));

    [Fact]
    public async Task Refuses_an_answer_larger_than_the_limit()
    {
        var body = "<ENVELOPE><COLLECTION>" + string.Concat(Enumerable.Repeat("<VOUCHER><NARRATION>" + new string('x', 600) + "</NARRATION></VOUCHER>", 4)) + "</COLLECTION></ENVELOPE>";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var error = await Should.ThrowAsync<InvalidDataException>(() => TallyVoucherReader.ReadAsync(stream, TestContext.Current.CancellationToken, maxBytes: 2048));

        error.Message.ShouldBe("Tally's answer is larger than 2 KB, more than HC360 reads for one day.");
    }
}
