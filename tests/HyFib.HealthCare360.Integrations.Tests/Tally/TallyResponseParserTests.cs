using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Tally;

public sealed class TallyResponseParserTests
{
    [Fact]
    public void Reads_counters_from_classic_responses()
    {
        const string xml = """
            <RESPONSE>
              <CREATED>2</CREATED><ALTERED>1</ALTERED><DELETED>0</DELETED><LASTVCHID>4521</LASTVCHID>
              <LASTMID>0</LASTMID><COMBINED>0</COMBINED><IGNORED>0</IGNORED><ERRORS>0</ERRORS><CANCELLED>0</CANCELLED>
            </RESPONSE>
            """;

        TallyResponseParser.Parse(xml).ShouldBe(new AccountingExportResult(true, 2, 1, 0, "Tally created 2 and altered 1 voucher(s)."));
    }

    [Fact]
    public void Reads_counters_from_tally_prime_import_results()
    {
        const string xml = """
            <ENVELOPE>
              <HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER>
              <BODY><DESC></DESC><DATA><IMPORTRESULT>
                <CREATED>1</CREATED><ALTERED>0</ALTERED><DELETED>0</DELETED><LASTVCHID>77</LASTVCHID>
                <COMBINED>0</COMBINED><IGNORED>0</IGNORED><ERRORS>0</ERRORS><CANCELLED>0</CANCELLED>
              </IMPORTRESULT></DATA></BODY>
            </ENVELOPE>
            """;

        var result = TallyResponseParser.Parse(xml);

        result.Success.ShouldBeTrue();
        result.Created.ShouldBe(1);
    }

    [Fact]
    public void Line_errors_become_the_message()
    {
        const string xml = """
            <RESPONSE>
              <LINEERROR>Ledger 'Cash Counter 9' does not exist!</LINEERROR>
              <CREATED>1</CREATED><ALTERED>0</ALTERED><ERRORS>1</ERRORS>
            </RESPONSE>
            """;

        TallyResponseParser.Parse(xml).ShouldBe(new AccountingExportResult(false, 1, 0, 1, "Ledger 'Cash Counter 9' does not exist!"));
    }

    [Fact]
    public void Counts_errors_even_without_a_line_error()
    {
        var result = TallyResponseParser.Parse("<RESPONSE><CREATED>0</CREATED><ERRORS>3</ERRORS></RESPONSE>");

        result.Success.ShouldBeFalse();
        result.Errors.ShouldBe(3);
        result.Message.ShouldBe("Tally reported 3 error(s).");
    }

    [Fact]
    public void A_failed_status_reports_the_body_text()
    {
        const string xml = """
            <ENVELOPE><HEADER><VERSION>1</VERSION><STATUS>0</STATUS></HEADER><BODY><DATA>Could not find Company 'Nana Hospitals'</DATA></BODY></ENVELOPE>
            """;

        var result = TallyResponseParser.Parse(xml);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Could not find Company 'Nana Hospitals'");
    }

    [Fact]
    public void Tolerates_the_control_character_references_tally_emits()
    {
        var result = TallyResponseParser.Parse("<RESPONSE><LINEERROR>Voucher totals do not match!&#4;</LINEERROR><CREATED>0</CREATED><ERRORS>1</ERRORS></RESPONSE>");

        result.Success.ShouldBeFalse();
        result.Message!.ShouldStartWith("Voucher totals do not match!");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tally.ERP 9 Server is Running")]
    [InlineData("<RESPONSE><CREATED>1</CREATED>")]
    public void Non_xml_answers_are_failures(string response)
    {
        var result = TallyResponseParser.Parse(response);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldBe(1);
        result.Message!.ShouldStartWith("Tally returned a response that is not XML");
    }

    [Fact]
    public void Refuses_document_type_definitions()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE RESPONSE [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <RESPONSE><CREATED>&xxe;</CREATED></RESPONSE>
            """;

        TallyResponseParser.TryLoad(xml, out _).ShouldBeFalse();
        TallyResponseParser.Parse(xml).Success.ShouldBeFalse();
    }
}
