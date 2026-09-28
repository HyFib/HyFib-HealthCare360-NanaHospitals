using System.Net;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Sms;

public sealed class Msg91SmsProviderTests
{
    private const string Template = "Dear {{name}}, your appointment with {{doctor}} is confirmed for {{date}} at {{time}}. Ref {{reference}}. - {{hospital}}";
    private const string Text = "Dear Ravi, your appointment with Dr. Rao is confirmed for 26 Sep at 10:30 AM. Ref APT-9. - Nana Hospitals";

    private static readonly SmsSettings Settings = new()
    {
        Enabled = true,
        Provider = "msg91",
        Msg91 = new Msg91Options { TemplateIds = new Dictionary<string, string> { ["1107160000000012345"] = "65f0c0ffee0123456789abcd" } },
    };

    private static readonly SmsSecrets Secrets = new() { Msg91AuthKey = "test-auth-key" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();
    private readonly ISmsTemplateCatalog _templates = Substitute.For<ISmsTemplateCatalog>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Msg91SmsProviderTests() =>
        _templates.BodiesForDltTemplateAsync("1107160000000012345", Arg.Any<CancellationToken>()).Returns(new List<string> { Template });

    private Msg91SmsProvider Provider => new(_http.Client(), _templates, _tracing.Trace);

    [Fact]
    public async Task Sends_flow_request_with_template_variables_recovered_from_the_text()
    {
        _http.Respond(HttpStatusCode.OK, """{"type":"success","message":"3263666f6e71373432323538"}""");

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", Text, "1107160000000012345", null, "log-1"), Settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("3263666f6e71373432323538"));
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://control.msg91.com/api/v5/flow"));
        request.Header("authkey").ShouldBe("test-auth-key");
        var body = request.Json();
        body.GetProperty("template_id").GetString().ShouldBe("65f0c0ffee0123456789abcd");
        var recipient = body.GetProperty("recipients")[0];
        recipient.GetProperty("mobiles").GetString().ShouldBe("919876543210");
        recipient.GetProperty("name").GetString().ShouldBe("Ravi");
        recipient.GetProperty("doctor").GetString().ShouldBe("Dr. Rao");
        recipient.GetProperty("reference").GetString().ShouldBe("APT-9");
    }

    [Fact]
    public async Task Logs_a_masked_summary_and_never_the_message_text_or_key()
    {
        _http.Respond(HttpStatusCode.OK, """{"type":"success","message":"req-1"}""");

        await Provider.SendAsync(new SmsMessage("+919876543210", Text, "1107160000000012345", null, "log-1"), Settings, Secrets, Ct);

        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Success.ShouldBeTrue();
        entry.Reference.ShouldBe("log-1");
        entry.Request!.ShouldContain("+91******3210");
        _tracing.AllText.ShouldNotContain("Ravi");
        _tracing.AllText.ShouldNotContain("test-auth-key");
    }

    [Fact]
    public async Task Reports_msg91_errors()
    {
        _http.Respond(HttpStatusCode.OK, """{"type":"error","message":"Invalid authkey"}""");

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", Text, "1107160000000012345"), Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("MSG91: Invalid authkey");
    }

    [Fact]
    public async Task Requires_a_dlt_template_id_before_calling_msg91()
    {
        var result = await Provider.SendAsync(new SmsMessage("+919876543210", Text), Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("DLT template id");
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_text_that_does_not_match_its_registered_template()
    {
        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Some other text", "1107160000000012345"), Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Falls_back_to_a_single_text_variable_when_configured()
    {
        _http.Respond(HttpStatusCode.OK, """{"type":"success","message":"req-2"}""");
        var settings = Settings with { Msg91 = Settings.Msg91 with { TextVariable = "message" } };

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Free text", "1107160000000099999"), settings, Secrets, Ct);

        result.Success.ShouldBeTrue();
        _http.Requests[0].Json().GetProperty("recipients")[0].GetProperty("message").GetString().ShouldBe("Free text");
    }

    [Fact]
    public async Task Transport_failures_become_a_failed_result()
    {
        _http.Throw(new HttpRequestException("connection refused"));

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", Text, "1107160000000012345"), Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("Could not reach MSG91");
    }
}
