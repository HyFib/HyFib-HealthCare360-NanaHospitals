using System.Net;
using System.Text;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Payments;

public sealed class RazorpayGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.FromHours(5.5));

    private static readonly PaymentSettings Settings = new() { Enabled = true, Provider = "razorpay", KeyId = "rzp_test_1DP5mmOlF5G5ag" };

    private static readonly PaymentSecrets Secrets = new() { KeySecret = "test-key-secret", WebhookSecret = "test-webhook-secret" };

    private static readonly CreatePaymentOrder Order = new(1500.50m, "inr", "BILL-2026-00042", "OPD consultation", "Ravi Kumar", "+919876543210", "ravi@example.com",
        new Dictionary<string, string> { ["billId"] = "42" });

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RazorpayGateway Gateway => new(_http.Client(), _tracing.Trace, new TestClock(Now));

    [Fact]
    public async Task Creates_an_order_in_paise_with_basic_auth()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"order_IluGWxBm9U8zJ8","entity":"order","amount":150050,"currency":"INR","status":"created"}""");

        var order = await Gateway.CreateOrderAsync(Order, Settings, Secrets, Ct);

        order.ShouldBe(new PaymentOrder("razorpay", "order_IluGWxBm9U8zJ8", 1500.50m, "INR", "rzp_test_1DP5mmOlF5G5ag", null));
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/orders"));
        request.Header("Authorization").ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("rzp_test_1DP5mmOlF5G5ag:test-key-secret")));
        var body = request.Json();
        body.GetProperty("amount").GetInt64().ShouldBe(150050);
        body.GetProperty("currency").GetString().ShouldBe("INR");
        body.GetProperty("receipt").GetString().ShouldBe("BILL-2026-00042");
        body.GetProperty("notes").GetProperty("billId").GetString().ShouldBe("42");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeTrue();
        _tracing.AllText.ShouldNotContain("test-key-secret");
        _tracing.AllText.ShouldNotContain("Ravi");
    }

    [Fact]
    public async Task Creates_a_payment_link_that_razorpay_does_not_notify_by_default()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"plink_Et5LJ6FxnGpXK7","short_url":"https://rzp.io/i/nxrHnLJ","status":"created"}""");

        var link = await Gateway.CreatePaymentLinkAsync(Order, Settings with { PaymentLinkCallbackUrl = "https://his.example.org/pay/done" }, Secrets, Ct);

        link.OrderId.ShouldBe("plink_Et5LJ6FxnGpXK7");
        link.PaymentLinkUrl.ShouldBe("https://rzp.io/i/nxrHnLJ");
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/payment_links"));
        var body = request.Json();
        body.GetProperty("reference_id").GetString().ShouldBe("BILL-2026-00042");
        body.GetProperty("customer").GetProperty("contact").GetString().ShouldBe("+919876543210");
        body.GetProperty("notify").GetProperty("sms").GetBoolean().ShouldBeFalse();
        body.GetProperty("expire_by").GetInt64().ShouldBe(Now.AddHours(72).ToUnixTimeSeconds());
        body.GetProperty("callback_url").GetString().ShouldBe("https://his.example.org/pay/done");
        body.GetProperty("callback_method").GetString().ShouldBe("get");
    }

    [Fact]
    public async Task Surfaces_razorpay_error_descriptions()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"error":{"code":"BAD_REQUEST_ERROR","description":"The receipt has already been used.","field":"receipt"}}""");

        var failure = await Should.ThrowAsync<IntegrationException>(() => Gateway.CreateOrderAsync(Order, Settings, Secrets, Ct));

        failure.Message.ShouldBe("Razorpay: The receipt has already been used.");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Describes_network_failures_without_leaking_details()
    {
        _http.Throw(new HttpRequestException("No such host is known. (api.razorpay.com:443)"));

        var failure = await Should.ThrowAsync<IntegrationException>(() => Gateway.CreateOrderAsync(Order, Settings, Secrets, Ct));

        failure.Message.ShouldStartWith("Could not reach Razorpay");
    }

    [Fact]
    public async Task Refuses_to_call_razorpay_without_credentials_or_with_invalid_amounts()
    {
        await Should.ThrowAsync<IntegrationException>(() => Gateway.CreateOrderAsync(Order, Settings, new PaymentSecrets(), Ct));
        await Should.ThrowAsync<IntegrationException>(() => Gateway.CreateOrderAsync(Order with { Amount = 0.50m }, Settings, Secrets, Ct));
        await Should.ThrowAsync<IntegrationException>(() => Gateway.CreateOrderAsync(Order, Settings with { BaseUrl = "http://api.razorpay.com" }, Secrets, Ct));

        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_order_status_from_its_payments()
    {
        _http.Respond(HttpStatusCode.OK, """
            {"entity":"collection","count":2,"items":[
              {"id":"pay_FailedAttempt01","status":"failed","amount":150050,"method":"card","error_description":"Card declined"},
              {"id":"pay_CapturedUpi001","status":"captured","amount":150050,"amount_refunded":0,"method":"upi"}]}
            """);

        var status = await Gateway.GetStatusAsync("order_IluGWxBm9U8zJ8", Settings, Secrets, Ct);

        _http.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/orders/order_IluGWxBm9U8zJ8/payments"));
        status.ShouldBe(new PaymentStatusResult("order_IluGWxBm9U8zJ8", GatewayPaymentStatus.Captured, "pay_CapturedUpi001", 1500.50m, "upi", null));
    }

    [Fact]
    public async Task Reads_payment_link_status()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"plink_Et5LJ6FxnGpXK7","status":"paid","payments":[{"payment_id":"pay_Et5LYvBwQkrRdB","status":"captured","amount":150050,"method":"upi"}]}""");

        var status = await Gateway.GetStatusAsync("plink_Et5LJ6FxnGpXK7", Settings, Secrets, Ct);

        _http.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/payment_links/plink_Et5LJ6FxnGpXK7"));
        status.Status.ShouldBe(GatewayPaymentStatus.Captured);
        status.PaymentId.ShouldBe("pay_Et5LYvBwQkrRdB");
    }

    [Theory]
    [InlineData("order_x/../../v1/payments")]
    [InlineData("bill-42")]
    public async Task Never_puts_untrusted_ids_into_razorpay_urls(string id)
    {
        (await Gateway.GetStatusAsync(id, Settings, Secrets, Ct)).Status.ShouldBe(GatewayPaymentStatus.Failed);
        (await Gateway.RefundAsync(id, 100m, "duplicate", Settings, Secrets, Ct)).Success.ShouldBeFalse();

        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refunds_in_paise_and_reports_failures_in_the_result()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"rfnd_FP8QHiV938haTz","entity":"refund","amount":50000,"status":"processed"}""")
            .Respond(HttpStatusCode.BadRequest, """{"error":{"code":"BAD_REQUEST_ERROR","description":"The refund amount provided is greater than amount captured"}}""");

        var refund = await Gateway.RefundAsync("pay_CapturedUpi001", 500m, "Duplicate payment", Settings, Secrets, Ct);
        var rejected = await Gateway.RefundAsync("pay_CapturedUpi001", 5000m, "Duplicate payment", Settings, Secrets, Ct);

        refund.ShouldBe(new RefundResult(true, "rfnd_FP8QHiV938haTz", null));
        _http.Requests[0].Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/payments/pay_CapturedUpi001/refund"));
        _http.Requests[0].Json().GetProperty("amount").GetInt64().ShouldBe(50_000);
        rejected.Success.ShouldBeFalse();
        rejected.Error.ShouldBe("Razorpay: The refund amount provided is greater than amount captured");
    }

    [Theory]
    [InlineData("""{"error":{"description":"Authentication failed"}}""", 401, "Razorpay: Authentication failed")]
    [InlineData("<html>Bad gateway</html>", 502, "Razorpay returned HTTP 502.")]
    public void Builds_staff_friendly_error_messages(string body, int status, string expected) =>
        RazorpayGateway.ErrorMessage(body, status).ShouldBe(expected);
}
