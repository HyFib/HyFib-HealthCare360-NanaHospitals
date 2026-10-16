using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The platform logger's second line of defence (SEC-10): whatever an adapter passes as a request or response summary,
/// phone numbers, e-mail addresses and credentials are masked before the row is stored.
/// </summary>
public sealed class IntegrationLogSanitizeTests
{
    [Theory]
    [InlineData("""{"to":"+919876543210"}""", """{"to":"+919*******10"}""")]
    [InlineData("to=9876543210&text=hi", "to=9876****10&text=hi")]
    [InlineData("""{"mobiles":"919876543210"}""", """{"mobiles":"9198******10"}""")]
    [InlineData("Sent to +919876543210 and +918765432109", "Sent to +919*******10 and +918*******09")]
    public void Phone_numbers_keep_only_their_first_and_last_digits(string payload, string expected) =>
        IntegrationLogger.Sanitize(payload).ShouldBe(expected);

    [Theory]
    [InlineData("""{"to":"ravi.kumar@example.com"}""", """{"to":"r***@example.com"}""")]
    [InlineData("Mail to a.b+reports@nanahospitals.co.in failed", "Mail to a***@nanahospitals.co.in failed")]
    public void Email_addresses_keep_their_first_letter_and_domain(string payload, string expected) =>
        IntegrationLogger.Sanitize(payload).ShouldBe(expected);

    [Theory]
    [InlineData("""{"api_key":"k3y-123"}""", """{"api_key":"***"}""")]
    [InlineData("""{"apiKey":"k3y-123"}""", """{"apiKey":"***"}""")]
    [InlineData("""{"authkey":"msg91-key"}""", """{"authkey":"***"}""")]
    [InlineData("""{"Authorization" : "Bearer abc.def"}""", """{"Authorization" : "***"}""")]
    [InlineData("""{"password":"S3cret","user":"nurse"}""", """{"password":"***","user":"nurse"}""")]
    [InlineData("""{"token":"t-1","id":"msg-9"}""", """{"token":"***","id":"msg-9"}""")]
    [InlineData("""{"access_token":"ya29.abc"}""", """{"access_token":"***"}""")]
    [InlineData("""{"key_secret":"rzp-secret"}""", """{"key_secret":"***"}""")]
    [InlineData("""{"client_secret":"who-secret","clientSecret":"x"}""", """{"client_secret":"***","clientSecret":"***"}""")]
    [InlineData("""{"webhookSecret":"whsec"}""", """{"webhookSecret":"***"}""")]
    public void Credentials_are_removed_entirely(string payload, string expected) =>
        IntegrationLogger.Sanitize(payload).ShouldBe(expected);

    [Theory]
    [InlineData("HTTP 200 id=msg-778")]
    [InlineData("""{"status":"sent","count":3,"templateId":"TPL_42"}""")]
    [InlineData("3 receipt(s)")]
    public void Ordinary_summaries_are_left_alone(string payload) => IntegrationLogger.Sanitize(payload).ShouldBe(payload);

    [Fact]
    public void Everything_is_masked_together_and_the_result_is_bounded()
    {
        var payload = $$"""{"to":"+919876543210","email":"ravi@example.com","token":"abc","pad":"{{new string('x', 9000)}}"}""";

        var sanitized = IntegrationLogger.Sanitize(payload)!;

        sanitized.ShouldStartWith("""{"to":"+919*******10","email":"r***@example.com","token":"***","pad":"x""");
        sanitized.Length.ShouldBe(8000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_stays_nothing(string? payload) => IntegrationLogger.Sanitize(payload).ShouldBe(payload);
}
