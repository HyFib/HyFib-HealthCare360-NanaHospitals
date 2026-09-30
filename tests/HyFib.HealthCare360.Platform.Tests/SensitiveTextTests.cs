using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class SensitiveTextTests
{
    [Theory]
    [InlineData("The 'To' number +919876543210 is not a valid phone number.", "The 'To' number ******3210 is not a valid phone number.")]
    [InlineData("Call 98765 43210 back", "Call ******3210 back")]
    [InlineData("Aadhaar 2345 6789 0123 on file", "Aadhaar XXXX-XXXX-0123 on file")]
    [InlineData("aadhaar=234567890123", "aadhaar=XXXX-XXXX-0123")]
    [InlineData("ABHA 91-2345-6789-0123 linked", "ABHA **-****-****-0123 linked")]
    [InlineData("ABHA address ravi.kumar@abdm", "ABHA address ***@abdm")]
    [InlineData("Mail to ravi.kumar@example.com failed", "Mail to r***@example.com failed")]
    public void Personal_identifiers_keep_only_enough_to_tell_them_apart(string text, string expected) =>
        SensitiveText.Redact(text).ShouldBe(expected);

    [Theory]
    [InlineData("GET /api/v1/sms/status?token=Ab12Cd34Ef56&id=7", "GET /api/v1/sms/status?token=***&id=7")]
    [InlineData("{\"password\":\"S3cret-Pass\",\"user\":\"nurse\"}", "{\"password\":\"***\",\"user\":\"nurse\"}")]
    [InlineData("Authorization: Bearer abcdefgh.ijklmnop", "Authorization: Bearer ***")]
    [InlineData("jwt eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl rejected", "jwt [token] rejected")]
    [InlineData("otp: 123456", "otp: ***")]
    [InlineData("{\"password\":\"correct horse battery staple\",\"user\":\"nurse\"}", "{\"password\":\"***\",\"user\":\"nurse\"}")]
    [InlineData("{\"password\": \"P@ss,word;x\"}", "{\"password\": \"***\"}")]
    [InlineData("{\"client_secret\":\"a\\\"b c\",\"id\":7}", "{\"client_secret\":\"***\",\"id\":7}")]
    [InlineData("login password='two words' refused", "login password='***' refused")]
    [InlineData("{\"password\":\"never closed, and more", "{\"password\":\"***\"")]
    [InlineData("Host=db;Password=S3cret;Database=hc360", "Host=db;Password=***;Database=hc360")]
    public void Secrets_are_removed_entirely(string text, string expected) =>
        SensitiveText.Redact(text).ShouldBe(expected);

    [Theory]
    [InlineData("Outbox message 0199b0c4-4b1c-7c3e-9d2f-234567890123 failed on attempt 2")]
    [InlineData("Applying migration '20260930090444_RefreshTokenRetirement'")]
    [InlineData("Bill BL-2026-000123 finalised for 1500.00 INR")]
    [InlineData("Refresh token replayed; session revoked")]
    [InlineData("Password policy: at least 12 characters")]
    public void Ids_and_ordinary_text_are_left_alone(string text) => SensitiveText.Redact(text).ShouldBe(text);
}
