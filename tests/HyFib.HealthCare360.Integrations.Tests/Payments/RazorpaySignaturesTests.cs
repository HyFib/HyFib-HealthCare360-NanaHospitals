using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Integrations.Payments;

namespace HyFib.HealthCare360.Integrations.Tests.Payments;

public sealed class RazorpaySignaturesTests
{
    private const string KeySecret = "test-key-secret";
    private const string WebhookSecret = "test-webhook-secret";

    /// <summary>Independent reference implementation (not the production helper) so the test cannot share its bug.</summary>
    private static string Sign(string secret, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void Verifies_a_checkout_signature_with_the_key_secret()
    {
        var payload = RazorpaySignatures.CheckoutPayload("order_IluGWxBm9U8zJ8", "pay_IH4NVgf4Dreq1l");

        payload.ShouldBe("order_IluGWxBm9U8zJ8|pay_IH4NVgf4Dreq1l");
        RazorpaySignatures.Verify(payload, Sign(KeySecret, payload), KeySecret, WebhookSecret).ShouldBeTrue();
        RazorpaySignatures.Verify(payload, Sign(KeySecret, payload).ToUpperInvariant(), KeySecret, WebhookSecret).ShouldBeTrue();
    }

    [Fact]
    public void Verifies_payment_link_callbacks_with_the_key_secret()
    {
        var payload = RazorpaySignatures.PaymentLinkPayload("plink_Et5LJ6FxnGpXK7", "BILL-2026-00042", "paid", "pay_Et5LYvBwQkrRdB");

        payload.ShouldBe("plink_Et5LJ6FxnGpXK7|BILL-2026-00042|paid|pay_Et5LYvBwQkrRdB");
        RazorpaySignatures.Verify(payload, Sign(KeySecret, payload), KeySecret, WebhookSecret).ShouldBeTrue();
    }

    [Fact]
    public void Verifies_webhook_bodies_with_the_webhook_secret()
    {
        const string body = """{"entity":"event","event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_X","amount":50000}}}}""";

        RazorpaySignatures.Verify(body, Sign(WebhookSecret, body), KeySecret, WebhookSecret).ShouldBeTrue();
    }

    [Fact]
    public void A_checkout_signature_can_never_be_replayed_as_a_webhook_and_vice_versa()
    {
        const string body = """{"event":"payment.captured"}""";
        RazorpaySignatures.Verify(body, Sign(KeySecret, body), KeySecret, WebhookSecret).ShouldBeFalse();

        const string checkout = "order_IluGWxBm9U8zJ8|pay_IH4NVgf4Dreq1l";
        RazorpaySignatures.Verify(checkout, Sign(WebhookSecret, checkout), KeySecret, WebhookSecret).ShouldBeFalse();
    }

    [Theory]
    [InlineData("order_IluGWxBm9U8zJ8|pay_TAMPERED00001")]
    [InlineData("order_IluGWxBm9U8zJ8|pay_IH4NVgf4Dreq1l ")]
    public void Rejects_tampered_payloads(string tampered) =>
        RazorpaySignatures.Verify(tampered, Sign(KeySecret, "order_IluGWxBm9U8zJ8|pay_IH4NVgf4Dreq1l"), KeySecret, WebhookSecret).ShouldBeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("abc")]
    [InlineData("00")]
    public void Rejects_malformed_signatures(string signature) =>
        RazorpaySignatures.Verify("order_A|pay_B", signature, KeySecret, WebhookSecret).ShouldBeFalse();

    [Fact]
    public void Fails_closed_when_the_secret_is_not_configured()
    {
        const string payload = "order_A|pay_B";

        RazorpaySignatures.Verify(payload, Sign(KeySecret, payload), keySecret: null, WebhookSecret).ShouldBeFalse();
        RazorpaySignatures.Verify("{}", Sign(WebhookSecret, "{}"), KeySecret, webhookSecret: "").ShouldBeFalse();
    }
}
