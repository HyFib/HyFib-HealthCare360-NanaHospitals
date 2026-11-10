using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
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
    private readonly IPaymentProvider _cashfree = Substitute.For<IPaymentProvider>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PaymentGatewayRouterTests()
    {
        _razorpay.Key.Returns("razorpay");
        _razorpay.Name.Returns("Razorpay");
        _razorpay.IsSetUp(Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>())
            .Returns(call => !string.IsNullOrWhiteSpace(call.ArgAt<PaymentSettings>(0).KeyId) && !string.IsNullOrWhiteSpace(call.ArgAt<PaymentSecrets>(1).KeySecret));
        _cashfree.Key.Returns("cashfree");
        _cashfree.Name.Returns("Cashfree");
        _cashfree.IsSetUp(Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>())
            .Returns(call => !string.IsNullOrWhiteSpace(call.ArgAt<PaymentSettings>(0).CashfreeClientId)
                             && !string.IsNullOrWhiteSpace(call.ArgAt<PaymentSecrets>(1).CashfreeClientSecret));
        _razorpay.VerifySignature(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PaymentSecrets>())
            .Returns(call => RazorpaySignatures.Verify(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<PaymentSecrets>(2).KeySecret, call.ArgAt<PaymentSecrets>(2).WebhookSecret));
        _settings.GetAsync<PaymentSecrets>("integrations.payment.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentSecrets { KeySecret = KeySecret, WebhookSecret = WebhookSecret });
        Configure(new PaymentSettings { Enabled = true, Provider = "Razorpay", KeyId = "rzp_test_1DP5mmOlF5G5ag" });
    }

    private PaymentGatewayRouter Router =>
        new([_razorpay, _cashfree], new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), Servers.Live, NullLogger<IntegrationSettingsReader>.Instance), TimeProvider.System);

    private static string Sign(string secret, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    private void Configure(PaymentSettings? settings) =>
        _settings.GetAsync<PaymentSettings>("integrations.payment", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public void Verifies_the_order_and_payment_id_convention_other_modules_use()
    {
        const string orderId = "order_IluGWxBm9U8zJ8";
        const string paymentId = "pay_IH4NVgf4Dreq1l";

        Router.VerifySignature($"{orderId}|{paymentId}", Sign(KeySecret, $"{orderId}|{paymentId}"), null, null).ShouldBeTrue();
        Router.VerifySignature($"{orderId}|{paymentId}", Sign(KeySecret, $"{orderId}|pay_other"), null, null).ShouldBeFalse();
    }

    [Fact]
    public void Verifies_webhook_bodies_with_the_webhook_secret()
    {
        const string body = """{"event":"payment.captured","payload":{"payment":{"entity":{"order_id":"order_IluGWxBm9U8zJ8"}}}}""";

        Router.VerifySignature(body, Sign(WebhookSecret, body), null, null).ShouldBeTrue();
        Router.VerifySignature(body, Sign(KeySecret, body), null, null).ShouldBeFalse();
    }

    [Fact]
    public void Late_callbacks_still_verify_after_the_gateway_was_disabled()
    {
        Configure(new PaymentSettings { Enabled = false, Provider = "razorpay" });

        Router.VerifySignature("order_A|pay_B", Sign(KeySecret, "order_A|pay_B"), null, null).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("carrier-pigeon")]
    public void Nothing_verifies_without_a_known_gateway(string? provider)
    {
        Configure(provider is null ? null : new PaymentSettings { Enabled = true, Provider = provider });

        Router.VerifySignature("order_A|pay_B", Sign(KeySecret, "order_A|pay_B"), null, null).ShouldBeFalse();
    }

    [Fact]
    public void Empty_payloads_and_signatures_never_verify()
    {
        Router.VerifySignature("", Sign(KeySecret, ""), null, null).ShouldBeFalse();
        Router.VerifySignature("order_A|pay_B", " ", null, null).ShouldBeFalse();
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

        var status = await Router.GetStatusAsync("order_A", null, null, Ct);
        var refund = await Router.RefundAsync(new GatewayPaymentRef(null, "pay_B", null), 100m, "duplicate", null, null, Ct);

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

        Router.VerifySignature(body, Sign("branch-webhook-secret", body), null, facility).ShouldBeTrue();
        Router.VerifySignature(body, Sign(WebhookSecret, body), null, facility).ShouldBeFalse();
        Router.VerifySignature(body, Sign(WebhookSecret, body), null, null).ShouldBeTrue();
    }

    [Fact]
    public async Task Settlements_are_still_read_after_online_payments_are_switched_off()
    {
        Configure(new PaymentSettings { Enabled = false, Provider = "razorpay", KeyId = "rzp_test_1DP5mmOlF5G5ag" });
        var day = new DateOnly(2026, 9, 30);
        PaymentSettlement settlement = new("setl_A1B2C3D4E5", "UTR1", PaymentSettlementStatus.Processed, 980m, 0m, 0m, DateTimeOffset.UnixEpoch, []);
        _razorpay.GetSettlementsAsync(day, Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>()).Returns([settlement]);

        var report = await Router.GetSettlementsAsync(day, null, null, Ct);

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

        var failing = await Router.GetSettlementsAsync(new DateOnly(2026, 9, 30), null, null, Ct);
        Configure(null);
        var notConfigured = await Router.GetSettlementsAsync(new DateOnly(2026, 9, 30), null, null, Ct);

        failing.Available.ShouldBeFalse();
        failing.Problem.ShouldBe("Razorpay: Authentication failed");
        notConfigured.Available.ShouldBeFalse();
        notConfigured.Problem!.ShouldContain("not configured");
    }

    [Fact]
    public async Task A_refund_status_the_gateway_cannot_give_stays_pending_with_the_reason()
    {
        _razorpay.GetRefundStatusAsync("rfnd_A1B2C3D4E5", Arg.Any<GatewayPaymentRef>(), Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>())
            .Returns<RefundStatusResult>(_ => throw new IntegrationException("Could not reach Razorpay."));

        var status = await Router.GetRefundStatusAsync("rfnd_A1B2C3D4E5", new GatewayPaymentRef(null, "pay_B", null), null, Ct);

        status.Status.ShouldBe(PaymentRefundStatus.Pending);
        status.Error.ShouldBe("Could not reach Razorpay.");
    }

    [Fact]
    public async Task After_a_switch_to_cashfree_razorpays_payments_are_still_checked_and_refunded_with_razorpays_account()
    {
        Configure(new PaymentSettings { Enabled = true, Provider = "cashfree", KeyId = "rzp_test_1DP5mmOlF5G5ag", CashfreeClientId = "TEST10182934a7c1f0" });
        _settings.GetAsync<CashfreeSecrets>("integrations.payment.cashfree.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new CashfreeSecrets { ClientSecret = "cf-client-secret" });
        _cashfree.CreatePaymentLinkAsync(Arg.Any<CreatePaymentOrder>(), Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentOrder("cashfree", "hcl_0199f0a1b2c3d4e5f60718293a", 10m, "INR", null, "https://payments.cashfree.com/links/x"));
        _razorpay.GetStatusAsync("order_IluGWxBm9U8zJ8", Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentStatusResult("order_IluGWxBm9U8zJ8", GatewayPaymentStatus.Captured, "pay_IH4NVgf4Dreq1l", 10m, "upi", null));
        _razorpay.RefundAsync(Arg.Any<GatewayPaymentRef>(), 10m, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(true, "rfnd_FP8QHiV938haTz", null));
        var payment = new GatewayPaymentRef("razorpay", "pay_IH4NVgf4Dreq1l", "order_IluGWxBm9U8zJ8");

        var link = await Router.CreatePaymentLinkAsync(new CreatePaymentOrder(10m, "INR", "BILL-1", "Bill", null, "9876543210", null), null, Ct);
        var status = await Router.GetStatusAsync("order_IluGWxBm9U8zJ8", "razorpay", null, Ct);
        var refund = await Router.RefundAsync(payment, 10m, "Duplicate", null, null, Ct);

        link.Provider.ShouldBe("cashfree");
        await _cashfree.Received(1).CreatePaymentLinkAsync(
            Arg.Any<CreatePaymentOrder>(), Arg.Any<PaymentSettings>(), Arg.Is<PaymentSecrets>(x => x.CashfreeClientSecret == "cf-client-secret"), Arg.Any<CancellationToken>());
        status.Status.ShouldBe(GatewayPaymentStatus.Captured);
        await _razorpay.Received(1).GetStatusAsync(
            "order_IluGWxBm9U8zJ8", Arg.Any<PaymentSettings>(), Arg.Is<PaymentSecrets>(x => x.KeySecret == KeySecret), Arg.Any<CancellationToken>());
        refund.ShouldBe(new RefundResult(true, "rfnd_FP8QHiV938haTz", null));
        await _razorpay.Received(1).RefundAsync(payment, 10m, "Duplicate", null, Arg.Any<PaymentSettings>(), Arg.Any<PaymentSecrets>(), Arg.Any<CancellationToken>());
        await _cashfree.DidNotReceiveWithAnyArgs().RefundAsync(default!, default, default!, default, default!, default!, Ct);
        (await Router.ProvidersAsync(null, Ct)).ShouldBe(["cashfree", "razorpay"]);
    }

    [Fact]
    public async Task A_gateway_whose_account_is_no_longer_set_up_is_said_so_rather_than_asked_with_another()
    {
        Configure(new PaymentSettings { Enabled = true, Provider = "cashfree", CashfreeClientId = "TEST10182934a7c1f0" });
        _settings.GetAsync<PaymentSecrets>("integrations.payment.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new PaymentSecrets());
        _settings.GetAsync<CashfreeSecrets>("integrations.payment.cashfree.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new CashfreeSecrets { ClientSecret = "cf-client-secret" });

        var status = await Router.GetStatusAsync("order_IluGWxBm9U8zJ8", "razorpay", null, Ct);
        var refund = await Router.RefundAsync(new GatewayPaymentRef("razorpay", "pay_IH4NVgf4Dreq1l", null), 10m, "Duplicate", null, null, Ct);
        var disputes = await Router.GetDisputesAsync(DateTimeOffset.UtcNow.AddDays(-30), "razorpay", null, Ct);

        status.ShouldSatisfyAllConditions(s => s.Status.ShouldBe(GatewayPaymentStatus.Pending), s => s.Error!.ShouldContain("Razorpay, whose account is no longer set up"));
        refund.ShouldSatisfyAllConditions(r => r.Success.ShouldBeFalse(), r => r.Error!.ShouldContain("Razorpay, whose account is no longer set up"));
        disputes.ShouldSatisfyAllConditions(d => d.Available.ShouldBeFalse(), d => d.Problem!.ShouldContain("Razorpay's account is no longer set up"));
        Router.VerifyWebhook("razorpay", "{}", "signature", null, null).ShouldBeNull();
        await _cashfree.DidNotReceiveWithAnyArgs().GetStatusAsync(default!, default!, default!, Ct);
        await _razorpay.DidNotReceiveWithAnyArgs().GetStatusAsync(default!, default!, default!, Ct);
        (await Router.ProvidersAsync(null, Ct)).ShouldBe(["cashfree"]);
    }

    [Fact]
    public void A_webhook_is_checked_with_the_account_of_the_gateway_that_sent_it()
    {
        Configure(new PaymentSettings { Enabled = true, Provider = "razorpay", KeyId = "rzp_test_1DP5mmOlF5G5ag", CashfreeClientId = "TEST10182934a7c1f0" });
        _settings.GetAsync<CashfreeSecrets>("integrations.payment.cashfree.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new CashfreeSecrets { ClientSecret = "cf-client-secret" });
        _cashfree.VerifyWebhook("{}", "cf-signature", "1759208112", Arg.Is<PaymentSecrets>(x => x.CashfreeClientSecret == "cf-client-secret"), Arg.Any<DateTimeOffset>())
            .Returns(true);
        _razorpay.VerifyWebhook(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<PaymentSecrets>(), Arg.Any<DateTimeOffset>())
            .Returns(call => RazorpaySignatures.Verify(call.ArgAt<string>(0), call.ArgAt<string>(1), null, call.ArgAt<PaymentSecrets>(3).WebhookSecret));
        const string body = """{"event":"payment.captured"}""";

        Router.VerifyWebhook("cashfree", "{}", "cf-signature", "1759208112", null).ShouldBe("cashfree");
        Router.VerifyWebhook("razorpay", body, Sign(WebhookSecret, body), null, null).ShouldBe("razorpay");
        Router.VerifyWebhook("cashfree", body, Sign(WebhookSecret, body), "1759208112", null).ShouldBeNull();
    }

    [Fact]
    public void A_delivery_to_the_shared_route_is_from_the_gateway_whose_account_signed_it()
    {
        // New payments go through Cashfree; Razorpay, used before, still has its account. The shared route's format names
        // no gateway (the training gateway signs it as Razorpay does), so the facility's gateway is tried, then the others.
        Configure(new PaymentSettings { Enabled = true, Provider = "cashfree", KeyId = "rzp_test_1DP5mmOlF5G5ag", CashfreeClientId = "TEST10182934a7c1f0" });
        _settings.GetAsync<CashfreeSecrets>("integrations.payment.cashfree.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new CashfreeSecrets { ClientSecret = "cf-client-secret" });
        _razorpay.VerifyWebhook(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<PaymentSecrets>(), Arg.Any<DateTimeOffset>())
            .Returns(call => RazorpaySignatures.Verify(call.ArgAt<string>(0), call.ArgAt<string>(1), null, call.ArgAt<PaymentSecrets>(3).WebhookSecret));
        const string body = """{"event":"payment.dispute.created"}""";

        Router.VerifyWebhook(null, body, Sign(WebhookSecret, body), null, null).ShouldBe("razorpay");
        Router.VerifyWebhook(null, body, Sign("someone-elses-secret", body), null, null).ShouldBeNull();
        _cashfree.Received().VerifyWebhook(body, Sign(WebhookSecret, body), null, Arg.Any<PaymentSecrets>(), Arg.Any<DateTimeOffset>());

        // Taken out of the settings, Razorpay no longer signs anything here.
        Configure(new PaymentSettings { Enabled = true, Provider = "cashfree", CashfreeClientId = "TEST10182934a7c1f0" });
        Router.VerifyWebhook(null, body, Sign(WebhookSecret, body), null, null).ShouldBeNull();
    }
}
