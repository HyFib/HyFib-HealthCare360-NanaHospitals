using System.Net;
using System.Text;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Sms;

public sealed class TwilioSmsProviderTests
{
    private const string Sid = "AC0123456789abcdef0123456789abcdef";

    private static readonly SmsSettings Settings = new()
    {
        Enabled = true,
        Provider = "twilio",
        Twilio = new TwilioOptions { AccountSid = Sid, From = "+15005550006", StatusCallbackUrl = "https://hc360.example.org/api/v1/integrations/webhooks/sms/twilio" },
    };

    private static readonly SmsSecrets Secrets = new() { TwilioAuthToken = "auth-token-123" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TwilioSmsProvider Provider => new(_http.Client(), _tracing.Trace);

    [Fact]
    public async Task Posts_the_message_form_with_basic_auth()
    {
        _http.Respond(HttpStatusCode.Created, """{"sid":"SM0123","status":"queued","body":"Your OTP is 4521","to":"+919876543210"}""");

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Your OTP is 4521"), Settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("SM0123"));
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri($"https://api.twilio.com/2010-04-01/Accounts/{Sid}/Messages.json"));
        request.Header("Authorization").ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Sid}:auth-token-123")));
        request.ContentType.ShouldBe("application/x-www-form-urlencoded");
        var body = request.Body.ShouldNotBeNull();
        body.ShouldContain("To=%2B919876543210");
        body.ShouldContain("From=%2B15005550006");
        body.ShouldContain("StatusCallback=https%3A%2F%2Fhc360.example.org");
    }

    [Fact]
    public async Task Never_logs_the_body_twilio_echoes_back()
    {
        _http.Respond(HttpStatusCode.Created, """{"sid":"SM0123","status":"queued","body":"Your OTP is 4521","to":"+919876543210"}""");

        await Provider.SendAsync(new SmsMessage("+919876543210", "Your OTP is 4521"), Settings, Secrets, Ct);

        _tracing.AllText.ShouldNotContain("4521");
        _tracing.AllText.ShouldNotContain("auth-token-123");
        _tracing.Entries.ShouldHaveSingleItem().Response!.ShouldContain("sid=SM0123");
    }

    [Fact]
    public async Task Reports_twilio_error_codes()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"code":21211,"message":"The 'To' number is not a valid phone number.","status":400}""");

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Hello"), Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("Twilio error 21211: The 'To' number is not a valid phone number.");
    }

    [Fact]
    public async Task Rejects_an_invalid_account_sid_without_calling_twilio()
    {
        var settings = Settings with { Twilio = Settings.Twilio with { AccountSid = "not-a-sid" } };

        var result = await Provider.SendAsync(new SmsMessage("+919876543210", "Hello"), settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Prefers_a_messaging_service_over_a_from_number()
    {
        _http.Respond(HttpStatusCode.Created, """{"sid":"SM9","status":"accepted"}""");
        var settings = Settings with { Twilio = Settings.Twilio with { MessagingServiceSid = "MG123" } };

        await Provider.SendAsync(new SmsMessage("+919876543210", "Hello"), settings, Secrets, Ct);

        _http.Requests[0].Body!.ShouldContain("MessagingServiceSid=MG123");
        _http.Requests[0].Body!.ShouldNotContain("From=");
    }
}
