using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Payments;

/// <summary>
/// The router is what other modules resolve as <see cref="IPaymentGateway"/>. Scheduling's portal prepayment and
/// Billing's checkout verify <c>"{orderId}|{paymentId}"</c> with the key secret (Razorpay convention), and Billing's
/// webhook passes the raw JSON body, which must verify with the webhook secret.
/// </summary>
public sealed class PaymentGatewayRouterTests
{
    private const string KeySecret = "test-key-secret";
    private const string WebhookSecret = "test-webhook-secret";

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IPaymentProvider _razorpay = Substitute.For<IPaymentProvider>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PaymentGatewayRouterTests()
    {
        _razorpay.Key.Returns("razorpay");
        _razorpay.VerifySignature(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PaymentSecrets>())
            .Returns(call => RazorpaySignatures.Verify(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<PaymentSecrets>(2).KeySecret, call.ArgAt<PaymentSecrets>(2).WebhookSecret));
        _settings.GetAsync<PaymentSecrets>("integrations.payment.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentSecrets { KeySecret = KeySecret, WebhookSecret = WebhookSecret });
        Configure(new PaymentSettings { Enabled = true, Provider = "Razorpay", KeyId = "rzp_test_1DP5mmOlF5G5ag" });
    }

    private PaymentGatewayRouter Router =>
        new([_razorpay], new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), NullLogger<IntegrationSettingsReader>.Instance));

    private static string Sign(string secret, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    private void Configure(PaymentSettings? settings) =>
        _settings.GetAsync<PaymentSettings>("integrations.payment", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public void Verifies_the_order_and_payment_id_convention_other_modules_use()
    {
        const string orderId = "order_IluGWxBm9U8zJ8";
        const string paymentId = "pay_IH4NVgf4Dreq1l";

        Router.VerifySignature($"{orderId}|{paymentId}", Sign(KeySecret, $"{orderId}|{paymentId}"), null).ShouldBeTrue();
        Router.VerifySignature($"{orderId}|{paymentId}", Sign(KeySecret, $"{orderId}|pay_other"), null).ShouldBeFalse();
    }

    [Fact]
    public void Verifies_webhook_bodies_with_the_webhook_secret()
    {
        const string body = """{"event":"payment.captured","payload":{"payment":{"entity":{"order_id":"order_IluGWxBm9U8zJ8"}}}}""";

        Router.VerifySignature(body, Sign(WebhookSecret, body), null).ShouldBeTrue();
        Router.VerifySignature(body, Sign(KeySecret, body), null).ShouldBeFalse();
    }

    [Fact]
    public void Late_callbacks_still_verify_after_the_gateway_was_disabled()
    {
        Configure(new PaymentSettings { Enabled = false, Provider = "razorpay" });

        Router.VerifySignature("order_A|pay_B", Sign(KeySecret, "order_A|pay_B"), null).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("carrier-pigeon")]
    public void Nothing_verifies_without_a_known_gateway(string? provider)
    {
        Configure(provider is null ? null : new PaymentSettings { Enabled = true, Provider = provider });

        Router.VerifySignature("order_A|pay_B", Sign(KeySecret, "order_A|pay_B"), null).ShouldBeFalse();
    }

    [Fact]
    public void Empty_payloads_and_signatures_never_verify()
    {
        Router.VerifySignature("", Sign(KeySecret, ""), null).ShouldBeFalse();
        Router.VerifySignature("order_A|pay_B", " ", null).ShouldBeFalse();
    }

    [Fact]
    public async Task Order_creation_fails_with_an_invalid_operation_when_the_gateway_is_disabled()
    {
        Configure(new PaymentSettings { Enabled = false, Provider = "razorpay" });

        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            Router.CreateOrderAsync(new CreatePaymentOrder(500m, "INR", "APT-1", "Consultation fee", null, null, null), null, Ct));

        failure.ShouldBeOfType<IntegrationException>().Message.ShouldBe("Payment gateway is disabled.");
    }

    [Fact]
    public async Task Status_and_refunds_report_configuration_problems_in_their_results()
    {
        Configure(null);

        var status = await Router.GetStatusAsync("order_A", null, Ct);
        var refund = await Router.RefundAsync("pay_B", 100m, "duplicate", null, Ct);

        status.Status.ShouldBe(GatewayPaymentStatus.Pending);
        status.Error!.ShouldContain("not configured");
        refund.Success.ShouldBeFalse();
    }

    [Fact]
    public void A_facility_with_its_own_account_verifies_with_its_own_webhook_secret()
    {
        var facility = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000a");
        _settings.GetAsync<PaymentSecrets>("integrations.payment.secret", facility, Arg.Any<CancellationToken>())
            .Returns(new PaymentSecrets { KeySecret = "branch-key-secret", WebhookSecret = "branch-webhook-secret" });
        const string body = "{\"event\":\"payment.captured\"}";

        Router.VerifySignature(body, Sign("branch-webhook-secret", body), facility).ShouldBeTrue();
        Router.VerifySignature(body, Sign(WebhookSecret, body), facility).ShouldBeFalse();
        Router.VerifySignature(body, Sign(WebhookSecret, body), null).ShouldBeTrue();
    }

    [Fact]
    public async Task Settlements_are_still_read_after_online_payments_are_switched_off()
    {
        Configure(new PaymentSettings { Enabled = false, Provider = "razorpay", KeyId = "rzp_test_1DP5mmOlF5G5ag" });
        var day = new DateOnly(2026, 9, 30);
        PaymentSettlement settlement = new("setl_A1B2C3D4E5", "UTR1", PaymentSettlementStatus.Processed, 980m, 0m, 0m, DateTimeOffset.UnixEpoch, []);
        _razorpay.GetSettlementsAsync(day, Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>()).Returns([settlement]);

        var report = await Router.GetSettlementsAsync(day, null, Ct);

        report.ShouldSatisfyAllConditions(
            r => r.Available.ShouldBeTrue(),
            r => r.Provider.ShouldBe("razorpay"),
            r => r.Settlements.ShouldHaveSingleItem().ShouldBe(settlement));
    }

    [Fact]
    public async Task Settlements_report_why_they_cannot_be_read()
    {
        _razorpay.GetSettlementsAsync(Arg.Any<DateOnly>(), Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PaymentSettlement>>(_ => throw new IntegrationException("Razorpay: Authentication failed"));

        var failing = await Router.GetSettlementsAsync(new DateOnly(2026, 9, 30), null, Ct);
        Configure(null);
        var notConfigured = await Router.GetSettlementsAsync(new DateOnly(2026, 9, 30), null, Ct);

        failing.Available.ShouldBeFalse();
        failing.Problem.ShouldBe("Razorpay: Authentication failed");
        notConfigured.Available.ShouldBeFalse();
        notConfigured.Problem!.ShouldContain("not configured");
    }

    [Fact]
    public async Task A_refund_status_the_gateway_cannot_give_stays_pending_with_the_reason()
    {
        _razorpay.GetRefundStatusAsync("rfnd_A1B2C3D4E5", Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>())
            .Returns<RefundStatusResult>(_ => throw new IntegrationException("Could not reach Razorpay."));

        var status = await Router.GetRefundStatusAsync("rfnd_A1B2C3D4E5", null, Ct);

        status.Status.ShouldBe(PaymentRefundStatus.Pending);
        status.Error.ShouldBe("Could not reach Razorpay.");
    }
}
