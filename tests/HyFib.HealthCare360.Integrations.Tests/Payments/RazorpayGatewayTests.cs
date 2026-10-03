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
        (await Gateway.RefundAsync(id, 100m, "duplicate", null, Settings, Secrets, Ct)).Success.ShouldBeFalse();

        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refunds_in_paise_and_reports_failures_in_the_result()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"rfnd_FP8QHiV938haTz","entity":"refund","amount":50000,"status":"processed"}""")
            .Respond(HttpStatusCode.BadRequest, """{"error":{"code":"BAD_REQUEST_ERROR","description":"The refund amount provided is greater than amount captured"}}""");

        var refund = await Gateway.RefundAsync("pay_CapturedUpi001", 500m, "Duplicate payment", null, Settings, Secrets, Ct);
        var rejected = await Gateway.RefundAsync("pay_CapturedUpi001", 5000m, "Duplicate payment", null, Settings, Secrets, Ct);

        refund.ShouldBe(new RefundResult(true, "rfnd_FP8QHiV938haTz", null));
        _http.Requests[0].Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/payments/pay_CapturedUpi001/refund"));
        _http.Requests[0].Json().GetProperty("amount").GetInt64().ShouldBe(50_000);
        _http.Requests[0].Header("X-Refund-Idempotency").ShouldBeEmpty();
        rejected.Success.ShouldBeFalse();
        rejected.Error.ShouldBe("Razorpay: The refund amount provided is greater than amount captured");
    }

    [Fact]
    public async Task Sends_the_idempotency_key_so_razorpay_refunds_a_repeated_request_once()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"rfnd_FP8QHiV938haTz","entity":"refund","amount":50000,"status":"pending"}""");

        var refund = await Gateway.RefundAsync("pay_CapturedUpi001", 500m, "Duplicate payment", "a1b2c3d4e5f60718293a4b5c6d7e8f90", Settings, Secrets, Ct);
        var refused = await Gateway.RefundAsync("pay_CapturedUpi001", 500m, "Duplicate payment", "not a key!", Settings, Secrets, Ct);

        refund.Success.ShouldBeTrue();
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Header("X-Refund-Idempotency").ShouldBe("a1b2c3d4e5f60718293a4b5c6d7e8f90");
        request.Json().GetProperty("receipt").GetString().ShouldBe("a1b2c3d4e5f60718293a4b5c6d7e8f90");
        refused.Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"error":{"description":"Authentication failed"}}""", 401, "Razorpay: Authentication failed")]
    [InlineData("<html>Bad gateway</html>", 502, "Razorpay returned HTTP 502.")]
    public void Builds_staff_friendly_error_messages(string body, int status, string expected) =>
        RazorpayGateway.ErrorMessage(body, status).ShouldBe(expected);

    [Fact]
    public async Task Reports_when_razorpay_took_the_payment()
    {
        _http.Respond(HttpStatusCode.OK, """
            {"entity":"collection","count":1,"items":[{"id":"pay_CapturedUpi001","status":"captured","amount":150050,"method":"upi","created_at":1759208112}]}
            """);

        var status = await Gateway.GetStatusAsync("order_IluGWxBm9U8zJ8", Settings, Secrets, Ct);

        status.PaidAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1759208112));
    }

    [Fact]
    public async Task Reads_a_days_settlements_with_their_transactions_and_headers()
    {
        _http.Respond(HttpStatusCode.OK, ReconPage)
            .Respond(HttpStatusCode.OK, """{"id":"setl_DGlQ1Rj8os78Ec","entity":"settlement","amount":2433500,"status":"processed","fees":0,"tax":0,"utr":"1568176960vxp0rj","created_at":1568176960}""");

        var settlements = await Gateway.GetSettlementsAsync(new DateOnly(2019, 9, 11), Settings, Secrets, Ct);

        _http.Requests[0].Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/settlements/recon/combined?year=2019&month=09&day=11&count=1000&skip=0"));
        _http.Requests[1].Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/settlements/setl_DGlQ1Rj8os78Ec"));
        _http.Requests.ShouldAllBe(r => r.Method == HttpMethod.Get && r.Header("Authorization").StartsWith("Basic "));
        var settlement = settlements.ShouldHaveSingleItem();
        settlement.ShouldSatisfyAllConditions(
            s => s.Utr.ShouldBe("1568176960vxp0rj"),
            s => s.Status.ShouldBe(PaymentSettlementStatus.Processed),
            s => s.Amount.ShouldBe(24_335m),
            s => s.SettledAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1568176960)));
        settlement.Items.Count.ShouldBe(2);
        settlement.Items[0].ShouldBe(new PaymentSettlementItem(
            "pay_DEXrnipqTmWVGE", PaymentTransactionType.Payment, null, "order_DEXrnRiR3SNDHA", 1_000m, 29m, 4.42m, 971m, 0m, "card",
            DateTimeOffset.FromUnixTimeSeconds(1567692556)));
        settlement.Items[1].ShouldSatisfyAllConditions(
            i => i.Type.ShouldBe(PaymentTransactionType.Refund),
            i => i.PaymentId.ShouldBe("pay_DEXq1pACSqFxtS"),
            i => i.Debit.ShouldBe(2_425m));
        _tracing.AllText.ShouldNotContain("MasterCard");
        _tracing.AllText.ShouldNotContain("Beam me up");
    }

    [Fact]
    public async Task Reads_every_page_of_a_busy_day()
    {
        var fullPage = "{\"entity\":\"collection\",\"count\":1000,\"items\":[" + string.Join(",", Enumerable.Range(0, 1000).Select(i =>
            $$"""{"entity_id":"pay_Page1Item{{i:D6}}","type":"payment","credit":9800,"debit":0,"amount":10000,"fee":200,"tax":0,"settled":true,"settlement_id":"setl_BusyDay000001","settlement_utr":"UTR0001"}""")) + "]}";
        _http.Respond(HttpStatusCode.OK, fullPage)
            .Respond(HttpStatusCode.OK, """{"entity":"collection","count":0,"items":[]}""")
            .Respond(HttpStatusCode.OK, """{"id":"setl_BusyDay000001","amount":9800000,"status":"processed","utr":"UTR0001","created_at":1759208112}""");

        var settlements = await Gateway.GetSettlementsAsync(new DateOnly(2026, 9, 30), Settings, Secrets, Ct);

        _http.Requests[1].Uri.Query.ShouldContain("skip=1000");
        settlements.ShouldHaveSingleItem().Items.Count.ShouldBe(1000);
    }

    [Fact]
    public async Task A_failed_call_fails_the_whole_day_so_nothing_half_read_is_stored()
    {
        _http.Respond(HttpStatusCode.OK, ReconPage).Respond(HttpStatusCode.InternalServerError, """{"error":{"description":"Something went wrong"}}""");

        var failure = await Should.ThrowAsync<IntegrationException>(() => Gateway.GetSettlementsAsync(new DateOnly(2019, 9, 11), Settings, Secrets, Ct));

        failure.Message.ShouldBe("Razorpay: Something went wrong");
    }

    [Fact]
    public async Task Follows_a_refund_until_the_bank_confirms_it()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"rfnd_FP8QHiV938haTz","amount":50000,"payment_id":"pay_CapturedUpi001","acquirer_data":{"arn":"74836287462837"},"status":"processed","created_at":1759208112}""")
            .Respond(HttpStatusCode.OK, """{"id":"rfnd_FP8QHiV938haTz","amount":50000,"payment_id":"pay_CapturedUpi001","acquirer_data":{"arn":null},"status":"pending"}""")
            .Respond(HttpStatusCode.OK, """{"id":"rfnd_FP8QHiV938haTz","amount":50000,"payment_id":"pay_CapturedUpi001","status":"failed"}""");

        var processed = await Gateway.GetRefundStatusAsync("rfnd_FP8QHiV938haTz", Settings, Secrets, Ct);
        var pending = await Gateway.GetRefundStatusAsync("rfnd_FP8QHiV938haTz", Settings, Secrets, Ct);
        var failed = await Gateway.GetRefundStatusAsync("rfnd_FP8QHiV938haTz", Settings, Secrets, Ct);

        _http.Requests[0].Uri.ShouldBe(new Uri("https://api.razorpay.com/v1/refunds/rfnd_FP8QHiV938haTz"));
        processed.ShouldBe(new RefundStatusResult(
            "rfnd_FP8QHiV938haTz", PaymentRefundStatus.Processed, 500m, "pay_CapturedUpi001", "74836287462837", DateTimeOffset.FromUnixTimeSeconds(1759208112), null));
        pending.Status.ShouldBe(PaymentRefundStatus.Pending);
        pending.Reference.ShouldBeNull();
        failed.Status.ShouldBe(PaymentRefundStatus.Failed);
        failed.Error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("rfnd_x/../../payments")]
    [InlineData("pay_CapturedUpi001")]
    public async Task Never_puts_untrusted_refund_ids_into_razorpay_urls(string id)
    {
        var status = await Gateway.GetRefundStatusAsync(id, Settings, Secrets, Ct);

        status.Status.ShouldBe(PaymentRefundStatus.Pending);
        status.Error.ShouldNotBeNull();
        _http.Requests.ShouldBeEmpty();
    }

    /// <summary>Razorpay's documented sample (a payment and a refund in one settlement), with an unsettled item added.</summary>
    private const string ReconPage = """
        {"entity":"collection","count":3,"items":[
          {"entity_id":"pay_DEXrnipqTmWVGE","type":"payment","debit":0,"credit":97100,"amount":100000,"currency":"INR","fee":2900,"tax":442,
           "on_hold":false,"settled":true,"created_at":1567692556,"settled_at":1568176960,"settlement_id":"setl_DGlQ1Rj8os78Ec","posted_at":null,
           "credit_type":"default","description":"Recurring Payment via Subscription","notes":"Beam me up Scotty.","payment_id":null,
           "settlement_utr":"1568176960vxp0rj","order_id":"order_DEXrnRiR3SNDHA","order_receipt":null,"method":"card","card_network":"MasterCard",
           "card_issuer":"KARB","card_type":"credit","dispute_id":null},
          {"entity_id":"rfnd_DGRcGzZSLyEdg1","type":"refund","debit":242500,"credit":0,"amount":242500,"currency":"INR","fee":0,"tax":0,
           "on_hold":false,"settled":true,"created_at":1568107224,"settled_at":1568176960,"settlement_id":"setl_DGlQ1Rj8os78Ec","posted_at":null,
           "credit_type":"default","description":null,"notes":"Beam me up Scotty.","payment_id":"pay_DEXq1pACSqFxtS","settlement_utr":"1568176960vxp0rj",
           "order_id":"order_DEXpmZgffXNvuI","order_receipt":null,"method":"card","card_network":"MasterCard","card_issuer":"KARB","card_type":"credit",
           "dispute_id":null},
          {"entity_id":"pay_NotSettledYet01","type":"payment","debit":0,"credit":0,"amount":5000,"fee":0,"tax":0,"settled":false,"settlement_id":null}
        ]}
        """;
}
