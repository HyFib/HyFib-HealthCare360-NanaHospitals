using System.Net;
using System.Xml.Linq;
using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Tally;

public sealed class TallyPrimeExporterTests
{
    private const string CompanyList = """
        <ENVELOPE><BODY><DATA><COLLECTION>
          <COMPANY NAME="Nana Hospitals Pvt Ltd"><NAME>Nana Hospitals Pvt Ltd</NAME></COMPANY>
        </COLLECTION></DATA></BODY></ENVELOPE>
        """;

    private static readonly TallySettings Settings = new() { Enabled = true, Provider = "tally", CompanyName = "Nana Hospitals Pvt Ltd", BatchSize = 2 };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TallyPrimeExporter Exporter => new(_http.Client(), _tracing.Trace);

    private static AccountingVoucher Receipt(int number) => new(
        "Receipt", new DateOnly(2026, 9, 25), $"RCPT-{number}", "Advance received",
        [new AccountingEntry("Cash", 500m, true), new AccountingEntry("Patient Advances", 500m, false)], $"RCPT-{number}");

    [Fact]
    public async Task Ping_succeeds_when_the_configured_company_is_open()
    {
        _http.Respond(HttpStatusCode.OK, CompanyList, "text/xml");

        (await Exporter.PingAsync(Settings, Ct)).ShouldBeTrue();

        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("http://localhost:9000/"));
        request.ContentType.ShouldBe("text/xml");
        XDocument.Parse(request.Body!).Root!.Element("HEADER")!.Element("TALLYREQUEST")!.Value.ShouldBe("Export");
    }

    [Fact]
    public async Task Ping_fails_when_another_company_is_open()
    {
        _http.Respond(HttpStatusCode.OK, CompanyList, "text/xml");

        (await Exporter.PingAsync(Settings with { CompanyName = "Other Clinic" }, Ct)).ShouldBeFalse();

        _tracing.Entries.ShouldHaveSingleItem().Error.ShouldBe("Company 'Other Clinic' is not open in Tally.");
    }

    [Fact]
    public async Task Ping_fails_quietly_when_tally_is_not_running()
    {
        _http.Throw(new HttpRequestException("No connection could be made because the target machine actively refused it."));

        (await Exporter.PingAsync(Settings, Ct)).ShouldBeFalse();

        _tracing.Entries.ShouldHaveSingleItem().Error!.ShouldStartWith("Could not reach Tally");
    }

    [Fact]
    public async Task Pushes_vouchers_in_batches_and_sums_the_counters()
    {
        _http.Respond(HttpStatusCode.OK, "<RESPONSE><CREATED>2</CREATED><ALTERED>0</ALTERED><ERRORS>0</ERRORS></RESPONSE>", "text/xml")
            .Respond(HttpStatusCode.OK, "<RESPONSE><CREATED>0</CREATED><ALTERED>1</ALTERED><ERRORS>0</ERRORS></RESPONSE>", "text/xml");

        var result = await Exporter.PushVouchersAsync([Receipt(1), Receipt(2), Receipt(3)], Settings, Ct);

        result.ShouldBe(new AccountingExportResult(true, 2, 1, 0, "Tally created 2 and altered 1 voucher(s)."));
        _http.Requests.Count.ShouldBe(2);
        XDocument.Parse(_http.Requests[0].Body!).Descendants("VOUCHER").Count().ShouldBe(2);
        XDocument.Parse(_http.Requests[1].Body!).Descendants("VOUCHER").ShouldHaveSingleItem().Attribute("REMOTEID")!.Value.ShouldBe("HC360-RCPT-3");
        _tracing.Entries.Select(e => e.Reference).ShouldBe(["RCPT-1..RCPT-2", "RCPT-3"]);
    }

    [Fact]
    public async Task Stops_and_counts_unsent_vouchers_as_errors_when_tally_goes_away()
    {
        _http.Respond(HttpStatusCode.OK, "<RESPONSE><CREATED>2</CREATED><ERRORS>0</ERRORS></RESPONSE>", "text/xml")
            .Throw(new HttpRequestException("connection refused"));

        var result = await Exporter.PushVouchersAsync([Receipt(1), Receipt(2), Receipt(3)], Settings, Ct);

        result.Success.ShouldBeFalse();
        result.Created.ShouldBe(2);
        result.Errors.ShouldBe(1);
        result.Message!.ShouldStartWith("Could not reach Tally");
    }

    [Fact]
    public async Task Validates_every_voucher_before_sending_anything()
    {
        var unbalanced = Receipt(2) with { Entries = [new AccountingEntry("Cash", 500m, true), new AccountingEntry("Patient Advances", 400m, false)] };

        var result = await Exporter.PushVouchersAsync([Receipt(1), unbalanced], Settings, Ct);

        result.ShouldBe(new AccountingExportResult(false, 0, 0, 1, "voucher RCPT-2: debits (500.00) and credits (400.00) do not balance"));
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_to_export_is_a_success() =>
        (await Exporter.PushVouchersAsync([], Settings, Ct)).ShouldBe(new AccountingExportResult(true, 0, 0, 0, "Nothing to export."));
}
