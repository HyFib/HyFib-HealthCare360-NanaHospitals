using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Sms;

namespace HyFib.HealthCare360.Integrations.Tests.Common;

public sealed class SignaturesTests
{
    private const string Fox = "The quick brown fox jumps over the lazy dog";

    [Fact]
    public void Hmac_sha256_hex_matches_the_published_test_vector() =>
        Signatures.HmacSha256Hex("key", Fox).ShouldBe("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");

    [Theory]
    [InlineData("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", true)]
    [InlineData("F7BC83F430538424B13298E6AA6FB143EF4D59A14946175997479DBC2D1A3CD8", true)]
    [InlineData("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd9", false)]
    [InlineData("f7bc83", false)]
    [InlineData("not-hex-at-all!", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Hmac_sha256_verification_is_exact_and_case_insensitive(string? signature, bool expected) =>
        Signatures.VerifyHmacSha256Hex("key", Fox, signature).ShouldBe(expected);

    [Fact]
    public void Verification_fails_without_a_secret() =>
        Signatures.VerifyHmacSha256Hex(null, Fox, "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8").ShouldBeFalse();

    [Fact]
    public void Hmac_sha1_base64_matches_the_published_test_vector() =>
        Signatures.HmacSha1Base64("key", Fox).ShouldBe(Convert.ToBase64String(Convert.FromHexString("de7c9b85b8b78aa6bc8a7a36f70a90701c9db4d9")));

    [Fact]
    public void Twilio_signature_matches_twilio_documentation_example()
    {
        var form = new Dictionary<string, string>
        {
            ["To"] = "+18005551212",
            ["CallSid"] = "CA1234567890ABCDE",
            ["Caller"] = "+12349013030",
            ["Digits"] = "1234",
            ["From"] = "+12349013030",
        };

        var payload = SmsDeliveryReceipts.TwilioSignaturePayload("https://mycompany.com/myapp.php?foo=1&bar=2", form);

        payload.ShouldBe("https://mycompany.com/myapp.php?foo=1&bar=2CallSidCA1234567890ABCDECaller+12349013030Digits1234From+12349013030To+18005551212");
        Signatures.VerifyHmacSha1Base64("12345", payload, "0/KCTR6DLpKmkAf8muzZqo1nDgQ=").ShouldBeTrue();
        Signatures.VerifyHmacSha1Base64("12345", payload.Replace("1234From", "9999From", StringComparison.Ordinal), "0/KCTR6DLpKmkAf8muzZqo1nDgQ=").ShouldBeFalse();
        Signatures.VerifyHmacSha1Base64("12345", payload, "%%%not-base64").ShouldBeFalse();
    }

    [Fact]
    public void Tokens_compare_in_constant_time_and_reject_missing_values()
    {
        Signatures.TokensEqual("s3cret-token", "s3cret-token").ShouldBeTrue();
        Signatures.TokensEqual("s3cret-token", "s3cret-tokeN").ShouldBeFalse();
        Signatures.TokensEqual("s3cret-token", "s3cret").ShouldBeFalse();
        Signatures.TokensEqual(null, "anything").ShouldBeFalse();
        Signatures.TokensEqual("", "").ShouldBeFalse();
        Signatures.TokensEqual("s3cret-token", null).ShouldBeFalse();
    }
}
