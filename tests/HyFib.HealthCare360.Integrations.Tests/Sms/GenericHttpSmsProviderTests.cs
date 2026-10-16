using System.Net;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Sms;

public sealed class GenericHttpSmsProviderTests
{
    private static readonly SmsSecrets Secrets = new() { GenericApiKey = "k3y-in-url" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GenericHttpSmsProvider Provider => new(_http.Client(), _tracing.Trace);

    [Fact]
    public async Task Query_string_gateways_get_encoded_values_and_the_key_stays_out_of_the_log()
    {
        _http.Respond(HttpStatusCode.OK, "OK 12345", "text/plain");
        var settings = new SmsSettings
        {
            Enabled = true,
            Provider = "generic",
            SenderId = "NANAHS",
            Generic = new GenericHttpSmsOptions
            {
                Method = "GET",
                Url = "https://sms.example.in/api/send?apikey={{apiKey}}&sender={{senderId}}&to={{to10}}&msg={{text}}&dlt={{dltTemplateId}}",
                SuccessContains = "OK",
            },
        };

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Report ready & signed", "1107160000000012345", "NANAHS"), settings, Secrets, Ct);

        result.Success.ShouldBeTrue();
        var uri = _http.Requests.ShouldHaveSingleItem().Uri.AbsoluteUri;
        uri.ShouldContain("apikey=k3y-in-url");
        uri.ShouldContain("to=9876543210");
        uri.ShouldContain("msg=Report%20ready%20%26%20signed");
        uri.ShouldContain("dlt=1107160000000012345");
        _tracing.AllText.ShouldNotContain("k3y-in-url");
        _tracing.AllText.ShouldContain("{{apiKey}}");
    }

    [Fact]
    public async Task Json_gateways_get_a_valid_body_and_the_message_id_is_read_from_the_response()
    {
        _http.Respond(HttpStatusCode.OK, """{"status":"success","data":{"id":"msg-778"}}""");
        var settings = new SmsSettings
        {
            Enabled = true,
            Provider = "generic",
            Generic = new GenericHttpSmsOptions
            {
                Url = "https://sms.example.in/v2/messages",
                BodyTemplate = """{"to":"{{to}}","text":"{{text}}"}""",
                ApiKeyHeader = "Authorization",
                ApiKeyPrefix = "Bearer ",
                MessageIdPath = "data.id",
            },
        };

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Dr. \"Rao\" says hi"), settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("msg-778"));
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Header("Authorization").ShouldBe("Bearer k3y-in-url");
        request.Json().GetProperty("text").GetString().ShouldBe("Dr. \"Rao\" says hi");
    }

    [Fact]
    public async Task A_200_without_the_success_marker_is_a_failure()
    {
        _http.Respond(HttpStatusCode.OK, "ERROR: invalid sender", "text/plain");
        var settings = new SmsSettings
        {
            Enabled = true,
            Provider = "generic",
            Generic = new GenericHttpSmsOptions { Method = "GET", Url = "https://sms.example.in/send?to={{to}}", SuccessContains = "OK" },
        };

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Hello"), settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"status":"success","data":{"id":"msg-901","to":"+919876543210","text":"Ravi Kumar, your HbA1c report is ready","key":"k3y-in-url"}}""")]
    [InlineData(HttpStatusCode.OK, "ERROR: sender NANAHS not approved for Ravi Kumar's report to 919876543210")]
    [InlineData(HttpStatusCode.BadRequest, "Invalid number 919876543210 for 'Ravi Kumar, your HbA1c report is ready'")]
    public async Task Only_the_status_and_message_id_of_the_gateways_answer_are_logged(HttpStatusCode status, string answer)
    {
        // Gateways echo the recipient, the text and sometimes the key, in forms the platform's masking cannot all spot.
        _http.Respond(status, answer, answer.StartsWith('{') ? "application/json" : "text/plain");
        var settings = new SmsSettings
        {
            Enabled = true,
            Provider = "generic",
            Generic = new GenericHttpSmsOptions
            {
                Url = "https://sms.example.in/v2/messages",
                BodyTemplate = """{"to":"{{to}}","text":"{{text}}"}""",
                SuccessContains = "success",
                MessageIdPath = "data.id",
            },
        };

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Ravi Kumar, your HbA1c report is ready", Reference: "ref-1"), settings, Secrets, Ct);

        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Response.ShouldBe(result.Success ? "HTTP 200 id=msg-901" : $"HTTP {(int)status}");
        _tracing.AllText.ShouldNotContain("Ravi");
        _tracing.AllText.ShouldNotContain("9876543210");
        _tracing.AllText.ShouldNotContain("k3y-in-url");
        (result.Error ?? string.Empty).ShouldNotContain("Ravi");
    }

    [Theory]
    [InlineData("ftp://sms.example.in/send")]
    [InlineData("not a url")]
    public async Task Only_http_endpoints_are_called(string url)
    {
        var settings = new SmsSettings { Enabled = true, Provider = "generic", Generic = new GenericHttpSmsOptions { Url = url } };

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Hello"), settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        _http.Requests.ShouldBeEmpty();
    }
}
