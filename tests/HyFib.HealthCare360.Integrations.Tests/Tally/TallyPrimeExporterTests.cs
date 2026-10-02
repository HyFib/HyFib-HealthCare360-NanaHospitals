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

    private static string Counts(int created, int altered, int errors, string? lineError = null) =>
        $"<RESPONSE>{(lineError is null ? "" : $"<LINEERROR>{lineError}</LINEERROR>")}<CREATED>{created}</CREATED><ALTERED>{altered}</ALTERED><ERRORS>{errors}</ERRORS></RESPONSE>";

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
    public async Task Pushes_vouchers_in_batches_and_answers_for_each_voucher()
    {
        _http.Respond(HttpStatusCode.OK, Counts(2, 0, 0), "text/xml").Respond(HttpStatusCode.OK, Counts(0, 1, 0), "text/xml");

        var result = await Exporter.PushVouchersAsync([Receipt(1), Receipt(2), Receipt(3)], Settings, Ct);

        result.Success.ShouldBeTrue();
        (result.Created, result.Altered, result.Errors).ShouldBe((2, 1, 0));
        result.Message.ShouldBe("Tally created 2 and altered 1 voucher(s).");
        result.Vouchers.ShouldBe([
            new AccountingVoucherAnswer("RCPT-1", AccountingVoucherOutcome.Created),
            new AccountingVoucherAnswer("RCPT-2", AccountingVoucherOutcome.Created),
            new AccountingVoucherAnswer("RCPT-3", AccountingVoucherOutcome.Altered),
        ]);
        _http.Requests.Count.ShouldBe(2);
        XDocument.Parse(_http.Requests[0].Body!).Descendants("VOUCHER").Count().ShouldBe(2);
        XDocument.Parse(_http.Requests[1].Body!).Descendants("VOUCHER").ShouldHaveSingleItem().Attribute("REMOTEID")!.Value.ShouldBe("HC360-RCPT-3");
        _tracing.Entries.Select(e => e.Reference).ShouldBe(["RCPT-1..RCPT-2", "RCPT-3"]);
    }

    [Fact]
    public async Task A_batch_with_mixed_counts_is_accepted_without_guessing_which_voucher_was_created()
    {
        _http.Respond(HttpStatusCode.OK, Counts(1, 1, 0), "text/xml");

        var result = await Exporter.PushVouchersAsync([Receipt(1), Receipt(2)], Settings, Ct);

        result.Success.ShouldBeTrue();
        result.Vouchers.Select(v => v.Outcome).ShouldBe([AccountingVoucherOutcome.Accepted, AccountingVoucherOutcome.Accepted]);
        _http.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sends_a_refused_batch_again_one_voucher_at_a_time_to_learn_which_voucher_tally_refused()
    {
        _http.Respond(HttpStatusCode.OK, Counts(1, 0, 1, "Ledger 'Card Settlement Account' does not exist!"), "text/xml")
            .Respond(HttpStatusCode.OK, Counts(0, 1, 0), "text/xml")
            .Respond(HttpStatusCode.OK, Counts(0, 0, 1, "Ledger 'Card Settlement Account' does not exist!"), "text/xml");

        var result = await Exporter.PushVouchersAsync([Receipt(1), Receipt(2)], Settings, Ct);

        result.Success.ShouldBeFalse();
        (result.Created, result.Altered, result.Errors).ShouldBe((0, 1, 1));
        result.Vouchers.ShouldBe([
            new AccountingVoucherAnswer("RCPT-1", AccountingVoucherOutcome.Altered),
            new AccountingVoucherAnswer("RCPT-2", AccountingVoucherOutcome.Rejected, "Ledger 'Card Settlement Account' does not exist!"),
        ]);
        result.Message.ShouldBe("voucher RCPT-2: Ledger 'Card Settlement Account' does not exist!");
        _http.Requests.Select(r => XDocument.Parse(r.Body!).Descendants("VOUCHER").Count()).ShouldBe([2, 1, 1]);
    }

    [Fact]
    public async Task A_voucher_tally_neither_created_nor_altered_is_unconfirmed()
    {
        _http.Respond(HttpStatusCode.OK, "<RESPONSE><CREATED>0</CREATED><ALTERED>0</ALTERED><IGNORED>1</IGNORED><ERRORS>0</ERRORS></RESPONSE>", "text/xml");

        var result = await Exporter.PushVouchersAsync([Receipt(1)], Settings with { BatchSize = 50 }, Ct);

        result.Success.ShouldBeFalse();
        result.Vouchers.ShouldHaveSingleItem().Outcome.ShouldBe(AccountingVoucherOutcome.Unconfirmed);
        result.Errors.ShouldBe(1);
    }

    [Fact]
    public async Task Stops_and_marks_unsent_vouchers_when_tally_goes_away()
    {
        _http.Respond(HttpStatusCode.OK, Counts(2, 0, 0), "text/xml").Throw(new HttpRequestException("connection refused"));

        var result = await Exporter.PushVouchersAsync([Receipt(1), Receipt(2), Receipt(3)], Settings, Ct);

        result.Success.ShouldBeFalse();
        result.Created.ShouldBe(2);
        result.Errors.ShouldBe(1);
        result.Message!.ShouldStartWith("Could not reach Tally");
        result.Vouchers[2].Outcome.ShouldBe(AccountingVoucherOutcome.NotSent);
        result.Vouchers[2].Message!.ShouldStartWith("Could not reach Tally");
    }

    [Fact]
    public async Task Validates_every_voucher_before_sending_anything()
    {
        var unbalanced = Receipt(2) with { Entries = [new AccountingEntry("Cash", 500m, true), new AccountingEntry("Patient Advances", 400m, false)] };

        var result = await Exporter.PushVouchersAsync([Receipt(1), unbalanced], Settings, Ct);

        result.Success.ShouldBeFalse();
        (result.Created, result.Altered, result.Errors).ShouldBe((0, 0, 1));
        result.Message.ShouldBe("voucher RCPT-2: debits (500.00) and credits (400.00) do not balance");
        result.Vouchers.Select(v => v.Outcome).ShouldBe([AccountingVoucherOutcome.NotSent, AccountingVoucherOutcome.Rejected]);
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_to_export_is_a_success() =>
        (await Exporter.PushVouchersAsync([], Settings, Ct)).ShouldBe(new AccountingExportResult(true, 0, 0, 0, "Nothing to export."));

    [Fact]
    public async Task Reads_back_a_day_after_checking_that_the_company_is_open()
    {
        _http.Respond(HttpStatusCode.OK, CompanyList, "text/xml").Respond(HttpStatusCode.OK, TallyFixtures.DayExport, "text/xml");

        var day = await Exporter.ReadDayAsync(new DateOnly(2026, 9, 25), Settings, Ct);

        day.Success.ShouldBeTrue();
        day.Vouchers.Count.ShouldBe(4);
        var request = XDocument.Parse(_http.Requests[1].Body!);
        request.Descendants("ID").ShouldHaveSingleItem().Value.ShouldBe(TallyXml.DayVoucherCollection);
        request.Descendants("SVFROMDATE").ShouldHaveSingleItem().Value.ShouldBe("20260925");
        request.Descendants("SVCURRENTCOMPANY").ShouldHaveSingleItem().Value.ShouldBe("Nana Hospitals Pvt Ltd");
        _tracing.Entries.Last().Response.ShouldBe("4 voucher(s), 3 from HC360");
    }

    [Fact]
    public async Task Does_not_read_a_day_from_a_company_that_is_not_open()
    {
        _http.Respond(HttpStatusCode.OK, CompanyList, "text/xml");

        var day = await Exporter.ReadDayAsync(new DateOnly(2026, 9, 25), Settings with { CompanyName = "Other Clinic" }, Ct);

        day.Success.ShouldBeFalse();
        day.Error.ShouldBe("Company 'Other Clinic' is not open in Tally.");
        _http.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_failed_export_is_an_error_not_a_day_without_vouchers()
    {
        _http.Respond(HttpStatusCode.OK, CompanyList, "text/xml")
            .Respond(HttpStatusCode.OK, "<ENVELOPE><HEADER><VERSION>1</VERSION><STATUS>0</STATUS></HEADER><BODY><DATA>Could not set 'SVCurrentCompany'</DATA></BODY></ENVELOPE>", "text/xml");

        var day = await Exporter.ReadDayAsync(new DateOnly(2026, 9, 25), Settings, Ct);

        day.Success.ShouldBeFalse();
        day.Error.ShouldBe("Tally could not export the day's vouchers: Could not set 'SVCurrentCompany'");
    }

    [Fact]
    public async Task An_answer_that_is_not_xml_cannot_be_read()
    {
        _http.Respond(HttpStatusCode.OK, CompanyList, "text/xml").Respond(HttpStatusCode.OK, "<ENVELOPE><BODY><VOUCHER>", "text/xml");

        var day = await Exporter.ReadDayAsync(new DateOnly(2026, 9, 25), Settings, Ct);

        day.Success.ShouldBeFalse();
        day.Error.ShouldBe("Tally returned an answer HC360 could not read as XML.");
    }
}
