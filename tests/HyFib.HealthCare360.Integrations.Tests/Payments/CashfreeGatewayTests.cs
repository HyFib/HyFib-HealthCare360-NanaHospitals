using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Payments;

/// <summary>
/// Cashfree's adapter against the shapes its official SDKs document (cashfree-pg Node 6.0.6, Python 6.0.1, API version
/// 2026-01-01): the guides' samples, with ids as numbers or strings, are the fixtures.
/// </summary>
public sealed class CashfreeGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.FromHours(5.5));

    private static readonly PaymentSettings Settings = new() { Enabled = true, Provider = "cashfree", CashfreeClientId = "TEST10182934a7c1f0", };

    private static readonly PaymentSecrets Secrets = new() { CashfreeClientSecret = "cfsk_ma_test_secret" };

    private static readonly CreatePaymentOrder Order = new(1500.50m, "inr", "BILL-2026-00042", "OPD consultation", "Ravi Kumar", "+919876543210", "ravi@example.com",
        new Dictionary<string, string> { ["billId"] = "42" });

    private const string LinkId = "hcl_0199f0a1b2c3d4e5f60718293a4b";

    private const string LinkOrderId = "CFPay_z64lc99v754g_1";

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CashfreeGateway Gateway => new(_http.Client(), _tracing.Trace, new TestClock(Now));

    [Fact]
    public async Task Makes_a_payment_link_hc360_names_with_cashfrees_headers_in_rupees()
    {
        // The Python guide's response (cf_link_id a number, as in its samples).
        _http.Respond(HttpStatusCode.OK, """
            {"cf_link_id":30111525,"link_status":"ACTIVE","link_currency":"INR","link_amount":1500.5,"link_amount_paid":0,
             "link_url":"https://payments.cashfree.com/links/z64lc99v754g","link_expiry_time":"2026-09-28T10:00:00+05:30"}
            """);

        var link = await Gateway.CreatePaymentLinkAsync(Order, Settings with { PaymentLinkCallbackUrl = "https://his.example.org/pay/done" }, Secrets, Ct);

        link.Provider.ShouldBe("cashfree");
        link.OrderId.ShouldMatch("^hcl_[a-f0-9]{28}$");
        link.PaymentLinkUrl.ShouldBe("https://payments.cashfree.com/links/z64lc99v754g");
        link.CheckoutKey.ShouldBeNull();
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://api.cashfree.com/pg/links"));
        request.Header("x-client-id").ShouldBe("TEST10182934a7c1f0");
        request.Header("x-client-secret").ShouldBe("cfsk_ma_test_secret");
        request.Header("x-api-version").ShouldBe("2026-01-01");
        Guid.TryParse(request.Header("x-request-id"), out _).ShouldBeTrue();
        var body = request.Json();
        body.GetProperty("link_id").GetString().ShouldBe(link.OrderId);
        body.GetProperty("link_amount").GetDecimal().ShouldBe(1500.50m);
        body.GetProperty("link_currency").GetString().ShouldBe("INR");
        body.GetProperty("link_partial_payments").GetBoolean().ShouldBeFalse();
        var customer = body.GetProperty("customer_details");
        customer.GetProperty("customer_phone").GetString().ShouldBe("9876543210");
        customer.GetProperty("customer_name").GetString().ShouldBe("Ravi Kumar");
        body.GetProperty("link_notify").GetProperty("send_sms").GetBoolean().ShouldBeFalse();
        body.GetProperty("link_expiry_time").GetString().ShouldBe("2026-09-28T10:00:00+05:30");
        body.GetProperty("link_notes").GetProperty("hc360_reference").GetString().ShouldBe("BILL-2026-00042");
        body.GetProperty("link_meta").GetProperty("return_url").GetString().ShouldBe("https://his.example.org/pay/done");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeTrue();
        _tracing.AllText.ShouldNotContain("cfsk_ma_test_secret");
        _tracing.AllText.ShouldNotContain("Ravi");
        _tracing.AllText.ShouldNotContain("9876543210");
    }

    [Fact]
    public async Task The_portals_checkout_is_cashfrees_hosted_page_for_a_link()
    {
        _http.Respond(HttpStatusCode.OK, """{"link_url":"https://payments.cashfree.com/links/abc123"}""");

        var checkout = await Gateway.CreateOrderAsync(Order, Settings, Secrets, Ct);

        checkout.PaymentLinkUrl.ShouldBe("https://payments.cashfree.com/links/abc123");
        _http.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://api.cashfree.com/pg/links"));
    }

    [Fact]
    public async Task A_link_is_not_made_without_the_patients_mobile_or_when_cashfree_answers_for_another()
    {
        await Should.ThrowAsync<IntegrationException>(() => Gateway.CreatePaymentLinkAsync(Order with { CustomerPhone = null }, Settings, Secrets, Ct));
        _http.Requests.ShouldBeEmpty();

        _http.Respond(HttpStatusCode.OK, """{"link_id":"hcl_ffffffffffffffffffffffffffff","link_url":"https://payments.cashfree.com/links/x"}""");
        (await Should.ThrowAsync<IntegrationException>(() => Gateway.CreatePaymentLinkAsync(Order, Settings, Secrets, Ct)))
            .Message.ShouldContain("another payment link");
    }

    [Fact]
    public async Task Surfaces_cashfrees_error_message()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"message":"link_amount : should be greater than 0","code":"link_amount_invalid","type":"invalid_request_error"}""");

        var failure = await Should.ThrowAsync<IntegrationException>(() => Gateway.CreatePaymentLinkAsync(Order, Settings, Secrets, Ct));

        failure.Message.ShouldBe("Cashfree: link_amount : should be greater than 0");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Fact]
    public async Task A_link_is_paid_when_one_of_its_orders_has_a_successful_payment()
    {
        Link("ACTIVE", "2026-09-28T10:00:00+05:30");
        LinkOrders(("PAID", LinkOrderId));
        // The guide's payment sample: cf_payment_id a number.
        _http.Respond(HttpStatusCode.OK, $$$"""
            [{"cf_payment_id":12376123,"order_id":"{{{LinkOrderId}}}","entity":"payment","payment_status":"SUCCESS","payment_amount":1500.5,
              "payment_time":"2026-09-25T09:41:02+05:30","payment_completion_time":"2026-09-25T09:41:31+05:30","payment_group":"upi",
              "payment_method":{"upi":{"channel":"collect","upi_id":"ravi@okbank"}},"bank_reference":"1234567890"}]
            """);

        var status = await Gateway.GetStatusAsync(LinkId, Settings, Secrets, Ct);

        status.ShouldBe(new PaymentStatusResult(LinkId, GatewayPaymentStatus.Captured, "12376123", 1500.5m, "upi", null,
            new DateTimeOffset(2026, 9, 25, 9, 41, 31, TimeSpan.FromHours(5.5))));
        _http.Requests.Select(r => r.Uri.PathAndQuery).ShouldBe([
            $"/pg/links/{LinkId}",
            $"/pg/links/{LinkId}/orders?status=ALL",
            $"/pg/orders/{LinkOrderId}/payments",
        ]);
        _tracing.AllText.ShouldNotContain("ravi@okbank");
    }

    [Theory]
    [InlineData("ACTIVE", "2026-09-28T10:00:00+05:30", "FAILED", GatewayPaymentStatus.Failed)]
    [InlineData("ACTIVE", "2026-09-28T10:00:00+05:30", "USER_DROPPED", GatewayPaymentStatus.Failed)]
    [InlineData("ACTIVE", "2026-09-28T10:00:00+05:30", "PENDING", GatewayPaymentStatus.Pending)]
    [InlineData("ACTIVE", "2026-09-28T10:00:00+05:30", null, GatewayPaymentStatus.Created)]
    [InlineData("ACTIVE", "2026-09-24T10:00:00+05:30", null, GatewayPaymentStatus.Expired)]
    [InlineData("EXPIRED", "2026-09-28T10:00:00+05:30", "FAILED", GatewayPaymentStatus.Expired)]
    [InlineData("CANCELLED", "2026-09-28T10:00:00+05:30", null, GatewayPaymentStatus.Cancelled)]
    public async Task Reads_where_an_unpaid_link_stands(string linkStatus, string expiry, string? attempt, GatewayPaymentStatus expected)
    {
        Link(linkStatus, expiry);
        if (attempt is null)
        {
            _http.Respond(HttpStatusCode.OK, "[]");
        }
        else
        {
            LinkOrders(("ACTIVE", LinkOrderId));
            _http.Respond(HttpStatusCode.OK, $$$"""
                [{"cf_payment_id":"5114911176","order_id":"{{{LinkOrderId}}}","payment_status":"{{{attempt}}}","payment_amount":1500.5,
                  "payment_message":"Transaction declined by the bank","payment_time":"2026-09-25T09:41:02+05:30"}]
                """);
        }

        var status = await Gateway.GetStatusAsync(LinkId, Settings, Secrets, Ct);

        status.Status.ShouldBe(expected);
        status.PaymentId.ShouldBeNull();
        if (expected == GatewayPaymentStatus.Failed)
        {
            status.Error.ShouldBe("Transaction declined by the bank");
        }
    }

    [Fact]
    public async Task Money_taken_is_never_read_as_an_expired_link()
    {
        Link("EXPIRED", "2026-09-24T10:00:00+05:30");
        LinkOrders(("PAID", LinkOrderId));
        _http.Respond(HttpStatusCode.OK, $$$"""[{"cf_payment_id":12376123,"order_id":"{{{LinkOrderId}}}","payment_status":"SUCCESS","payment_amount":1500.5}]""");

        (await Gateway.GetStatusAsync(LinkId, Settings, Secrets, Ct)).Status.ShouldBe(GatewayPaymentStatus.Captured);
    }

    [Theory]
    [InlineData("order_IluGWxBm9U8zJ8")]
    [InlineData("hcl_../../orders/x")]
    [InlineData("hcl_0199F0A1B2C3D4E5F60718293A")]
    public async Task Never_puts_an_id_it_did_not_make_into_a_url(string orderId)
    {
        (await Gateway.GetStatusAsync(orderId, Settings, Secrets, Ct)).Status.ShouldBe(GatewayPaymentStatus.Failed);
        (await Gateway.RefundAsync(new GatewayPaymentRef("cashfree", "12376123", orderId), 100m, "Duplicate", "a1b2c3d4e5f60718293a4b5c6d7e8f90", Settings, Secrets, Ct))
            .Success.ShouldBeFalse();
        (await Gateway.RefundAsync(new GatewayPaymentRef("cashfree", "1237/../6123", LinkId), 100m, "Duplicate", null, Settings, Secrets, Ct))
            .Success.ShouldBeFalse();
        await Should.ThrowAsync<IntegrationException>(() => Gateway.GetDisputeAsync("56/../../orders", Settings, Secrets, Ct));
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refunds_on_the_links_paid_order_with_hc360s_key_as_the_refund_id()
    {
        const string key = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        PaidOrderOfLink();
        _http.Respond(HttpStatusCode.OK, Refund(key, "PENDING"));
        PaidOrderOfLink();
        _http.Respond(HttpStatusCode.OK, Refund(key, "PENDING"));

        var first = await Gateway.RefundAsync(new GatewayPaymentRef("cashfree", "12376123", LinkId), 500m, "Bill cancelled", key, Settings, Secrets, Ct);
        var again = await Gateway.RefundAsync(new GatewayPaymentRef("cashfree", "12376123", LinkId), 500m, "Bill cancelled", key, Settings, Secrets, Ct);

        first.ShouldBe(new RefundResult(true, key, null));
        again.ShouldBe(first);
        var posts = _http.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        posts.Select(r => r.Uri.PathAndQuery).ShouldAllBe(p => p == $"/pg/orders/{LinkOrderId}/refunds");
        var body = posts[0].Json();
        body.GetProperty("refund_id").GetString().ShouldBe(key);
        body.GetProperty("refund_amount").GetDecimal().ShouldBe(500m);
        body.GetProperty("refund_note").GetString().ShouldBe("Bill cancelled");
        body.GetProperty("refund_speed").GetString().ShouldBe("STANDARD");
        // The same refund carries the same x-idempotency-key, a UUID, on every attempt.
        Guid.TryParse(posts[0].Header("x-idempotency-key"), out _).ShouldBeTrue();
        posts[1].Header("x-idempotency-key").ShouldBe(posts[0].Header("x-idempotency-key"));
    }

    [Fact]
    public async Task A_refund_cashfree_already_holds_is_read_back_not_made_again()
    {
        const string key = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        PaidOrderOfLink();
        _http.Respond(HttpStatusCode.Conflict, """{"message":"Resource already present","code":"refund_already_exists","type":"invalid_request_error"}""");
        _http.Respond(HttpStatusCode.OK, Refund(key, "SUCCESS"));

        var refund = await Gateway.RefundAsync(new GatewayPaymentRef("cashfree", "12376123", LinkId), 500m, "Bill cancelled", key, Settings, Secrets, Ct);

        refund.ShouldBe(new RefundResult(true, key, null));
        _http.Requests.Count(r => r.Method == HttpMethod.Post).ShouldBe(1);
        _http.Requests[^1].Uri.PathAndQuery.ShouldBe($"/pg/orders/{LinkOrderId}/refunds/{key}");
    }

    [Fact]
    public async Task A_payment_the_link_never_took_is_not_refunded()
    {
        _http.Respond(HttpStatusCode.OK, "[]");

        var refund = await Gateway.RefundAsync(new GatewayPaymentRef("cashfree", "12376123", LinkId), 500m, "Bill cancelled", null, Settings, Secrets, Ct);

        refund.Success.ShouldBeFalse();
        refund.Error!.ShouldContain("no successful payment 12376123");
        _http.Requests.ShouldAllBe(r => r.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("SUCCESS", PaymentRefundStatus.Processed)]
    [InlineData("PENDING", PaymentRefundStatus.Pending)]
    [InlineData("ONHOLD", PaymentRefundStatus.Pending)]
    [InlineData("CANCELLED", PaymentRefundStatus.Failed)]
    [InlineData("FAILED", PaymentRefundStatus.Failed)]
    public async Task Reads_where_a_refund_stands(string status, PaymentRefundStatus expected)
    {
        const string key = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        PaidOrderOfLink();
        _http.Respond(HttpStatusCode.OK, Refund(key, status));

        var read = await Gateway.GetRefundStatusAsync(key, new GatewayPaymentRef("cashfree", "12376123", LinkId), Settings, Secrets, Ct);

        read.Status.ShouldBe(expected);
        read.Amount.ShouldBe(500m);
        read.PaymentId.ShouldBe("2388816360");
        read.Reference.ShouldBe(status == "SUCCESS" ? "RF12312" : null);
        (read.Error is not null).ShouldBe(expected == PaymentRefundStatus.Failed);
        _http.Requests[^1].Uri.PathAndQuery.ShouldBe($"/pg/orders/{LinkOrderId}/refunds/{key}");
    }

    [Fact]
    public async Task Reads_the_settlements_paid_on_a_day_with_their_events()
    {
        // Node 6.0.6's settlement list: one paid on the day asked, one the next day.
        _http.Respond(HttpStatusCode.OK, """
            {"cursor":null,"limit":2,"data":[
              {"cf_settlement_id":"6121238","status":"SUCCESS","settlement_utr":"N87912312","settlement_processed_on":"2026-09-25T12:57:52+05:30",
               "amount_settled":1460.2,"settlement_charge":2,"settlement_tax":0.36},
              {"cf_settlement_id":"6121239","status":"SUCCESS","settlement_utr":"N87912313","settlement_processed_on":"2026-09-26T00:00:01+05:30","amount_settled":10}]}
            """);
        _http.Respond(HttpStatusCode.OK, $$$"""
            {"cursor":"","limit":1000,"data":[
              {"event_details":{"event_id":"9001","event_type":"PAYMENT","event_amount":1500.5,"event_settlement_amount":1471.0,"event_service_charge":25,
                                "event_service_tax":4.5,"event_time":"2026-09-24T09:41:31+05:30"},
               "order_details":{"order_id":"{{{LinkOrderId}}}"},"payment_details":{"cf_payment_id":"12376123","payment_group":"UPI"},
               "settlement_details":{"cf_settlement_id":"6121238","settlement_utr":"N87912312"}},
              {"event_details":{"event_id":"9002","event_type":"REFUND","event_amount":10,"event_settlement_amount":-10,"event_time":"2026-09-24T10:00:00+05:30"},
               "order_details":{"order_id":"{{{LinkOrderId}}}"},"payment_details":{"cf_payment_id":"12376123"},
               "refund_details":[{"refund_id":"a1b2c3d4e5f60718293a4b5c6d7e8f90","refund_arn":"RF1"}]},
              {"event_details":{"event_id":"272","event_type":"CHARGEBACK","event_amount":100,"event_settlement_amount":-100},
               "order_details":{"order_id":"{{{LinkOrderId}}}"},"payment_details":{"cf_payment_id":"12376123"},"dispute_details":[{"dispute_category":"10.4 Fraud"}]},
              {"event_details":{"event_id":"273","event_type":"CHARGEBACK_REVERSAL","event_amount":100,"event_settlement_amount":100},
               "order_details":{"order_id":"{{{LinkOrderId}}}"},"payment_details":{"cf_payment_id":"12376123"}}]}
            """);

        var settlements = await Gateway.GetSettlementsAsync(new DateOnly(2026, 9, 25), Settings, Secrets, Ct);

        var settlement = settlements.ShouldHaveSingleItem();
        settlement.ShouldSatisfyAllConditions(
            s => s.SettlementId.ShouldBe("6121238"),
            s => s.Utr.ShouldBe("N87912312"),
            s => s.Status.ShouldBe(PaymentSettlementStatus.Processed),
            s => s.Amount.ShouldBe(1460.2m),
            s => s.Fees.ShouldBe(2.36m),
            s => s.Tax.ShouldBe(0.36m));
        settlement.Items.ShouldBe([
            new PaymentSettlementItem("12376123", PaymentTransactionType.Payment, "12376123", LinkOrderId, 1500.5m, 29.5m, 4.5m, 1471.0m, 0m, "upi",
                new DateTimeOffset(2026, 9, 24, 9, 41, 31, TimeSpan.FromHours(5.5))),
            new PaymentSettlementItem("a1b2c3d4e5f60718293a4b5c6d7e8f90", PaymentTransactionType.Refund, "12376123", LinkOrderId, 10m, 0m, 0m, 0m, 10m, null,
                new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(5.5))),
            new PaymentSettlementItem("cfevt_chargeback_272", PaymentTransactionType.Adjustment, "12376123", LinkOrderId, 100m, 0m, 0m, 0m, 100m, null, null),
            new PaymentSettlementItem("cfevt_chargeback_reversal_273", PaymentTransactionType.Adjustment, "12376123", LinkOrderId, 100m, 0m, 0m, 100m, 0m, null, null),
        ]);
        var list = _http.Requests[0];
        list.Uri.PathAndQuery.ShouldBe("/pg/settlements");
        list.Json().GetProperty("pagination").GetProperty("limit").GetInt32().ShouldBe(1000);
        list.Json().GetProperty("filters").GetProperty("start_date").GetString().ShouldBe("2026-09-24T00:00:00+05:30");
        var recon = _http.Requests[1];
        recon.Uri.PathAndQuery.ShouldBe("/pg/settlement/recon");
        recon.Json().GetProperty("filters").GetProperty("cf_settlement_ids")[0].GetInt64().ShouldBe(6121238);
    }

    [Fact]
    public async Task Reads_the_python_sdks_shape_of_a_settlements_events_too()
    {
        _http.Respond(HttpStatusCode.OK, """{"data":[{"cf_settlement_id":6121238,"status":"SUCCESS","utr":"N87912312","settlement_processed_on":"2026-09-25 12:57:52","amount_settled":10}]}""");
        _http.Respond(HttpStatusCode.OK, """
            {"data":[{"event_details":{"event_id":"9002","event_type":"REFUND","event_amount":10,"event_settlement_amount":-10,"event_remarks":0},
                      "payment_details":{"cf_payment_id":12376123,"payment_mode":"credit_card"},"refund_details":{"refund_id":"a1b2c3d4"}}]}
            """);

        var settlement = (await Gateway.GetSettlementsAsync(new DateOnly(2026, 9, 25), Settings, Secrets, Ct)).ShouldHaveSingleItem();

        settlement.Utr.ShouldBe("N87912312");
        settlement.SettledAt.ShouldBe(new DateTimeOffset(2026, 9, 25, 12, 57, 52, TimeSpan.FromHours(5.5)));
        var refund = settlement.Items.ShouldHaveSingleItem();
        refund.ShouldSatisfyAllConditions(
            r => r.EntityId.ShouldBe("a1b2c3d4"),
            r => r.Type.ShouldBe(PaymentTransactionType.Refund),
            r => r.PaymentId.ShouldBe("12376123"),
            r => r.Debit.ShouldBe(10m),
            r => r.Method.ShouldBe("credit_card"));
    }

    [Fact]
    public async Task Every_page_is_read_and_a_failed_page_fails_the_day()
    {
        var page = string.Join(",", Enumerable.Range(1, 1000).Select(i => $$$"""{"cf_settlement_id":"{{{9000000 + i}}}","status":"PENDING","settlement_initiated_on":"2026-09-20T10:00:00+05:30"}"""));
        _http.Respond(HttpStatusCode.OK, $$$"""{"cursor":"eyJwYWdlIjoyfQ==","limit":1000,"data":[{{{page}}}]}""");
        _http.Respond(HttpStatusCode.InternalServerError, """{"message":"Error at Cashfree's server.","type":"api_error"}""");

        (await Should.ThrowAsync<IntegrationException>(() => Gateway.GetSettlementsAsync(new DateOnly(2026, 9, 25), Settings, Secrets, Ct)))
            .Message.ShouldBe("Cashfree: Error at Cashfree's server.");
        _http.Requests.Count.ShouldBe(2);
        _http.Requests[1].Json().GetProperty("pagination").GetProperty("cursor").GetString().ShouldBe("eyJwYWdlIjoyfQ==");
    }

    [Fact]
    public async Task Reads_a_dispute_as_the_sdk_describes_it()
    {
        // The Python SDK's DisputesEntity fixture (ids and phone as numbers), with a deadline and a reason code.
        _http.Respond(HttpStatusCode.OK, """
            {"dispute_id":56,"dispute_type":"CHARGEBACK","reason_code":"10.4","reason_description":"Fraud","dispute_amount":10.0,
             "dispute_amount_currency":"INR","created_at":"2026-09-20T11:00:00+05:30","respond_by":"2026-09-30T23:59:59+05:30",
             "updated_at":"2026-09-21T11:00:00+05:30","resolved_at":"","dispute_status":"CHARGEBACK_UNDER_REVIEW","cf_dispute_remarks":"",
             "dispute_evidence":[{"document_id":18150,"document_name":"disputeSampleFile.pdf","document_type":"DeliveryProof"}],
             "order_details":{"order_id":"Load_test_0103_FGA4HF12AC","order_currency":"INR","order_amount":10.0,"cf_payment_id":1489901523,"payment_amount":10.0},
             "customer_details":{"customer_name":"Manideep Ellur","customer_phone":8281554863,"customer_email":"manideep.ellur@cashfree.com"}}
            """);

        var dispute = await Gateway.GetDisputeAsync("56", Settings, Secrets, Ct);

        dispute.ShouldBe(new GatewayDispute(
            "56", "1489901523", 10.0m, "10.4", "chargeback", GatewayDisputeStatus.UnderReview, "under_review",
            new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.FromHours(5.5)), new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.FromHours(5.5)),
            new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.FromHours(5.5))));
        _http.Requests.ShouldHaveSingleItem().Uri.PathAndQuery.ShouldBe("/pg/disputes/56");
        _tracing.AllText.ShouldNotContain("Manideep");
    }

    [Theory]
    [InlineData("DISPUTE_CREATED", GatewayDisputeStatus.Open, "created")]
    [InlineData("CHARGEBACK_DOCS_RECEIVED", GatewayDisputeStatus.UnderReview, "docs_received")]
    [InlineData("PRE_ARBITRATION_MERCHANT_WON", GatewayDisputeStatus.Won, "merchant_won")]
    [InlineData("ARBITRATION_MERCHANT_LOST", GatewayDisputeStatus.Lost, "merchant_lost")]
    [InlineData("RETRIEVAL_MERCHANT_ACCEPTED", GatewayDisputeStatus.Lost, "merchant_accepted")]
    [InlineData("DISPUTE_INSUFFICIENT_EVIDENCE", GatewayDisputeStatus.Other, "insufficient_evidence")]
    [InlineData("CHARGEBACK_SOMETHING_NEW", GatewayDisputeStatus.Other, "something_new")]
    public void Reads_where_a_dispute_stands(string status, GatewayDisputeStatus expected, string stage)
    {
        var type = status.StartsWith("PRE_ARBITRATION", StringComparison.Ordinal) ? "PRE_ARBITRATION" : status[..status.IndexOf('_', StringComparison.Ordinal)];
        using var document = JsonDocument.Parse($$$"""
            {"dispute_id":"77","dispute_type":"{{{type}}}","dispute_status":"{{{status}}}","dispute_amount":5,"order_details":{"cf_payment_id":"12376123"}}
            """);

        var dispute = CashfreeReading.Dispute(document.RootElement).ShouldNotBeNull();

        dispute.Status.ShouldBe(expected);
        dispute.StatusText.ShouldBe(stage);
        dispute.Phase.ShouldBe(type.ToLowerInvariant());
    }

    [Fact]
    public async Task Finds_new_disputes_through_the_accounts_dispute_events_of_the_last_days()
    {
        _http.Respond(HttpStatusCode.OK, $$$"""
            {"cursor":null,"data":[
              {"event_details":{"event_type":"PAYMENT"},"order_details":{"order_id":"{{{LinkOrderId}}}"}},
              {"event_details":{"event_type":"CHARGEBACK"},"order_details":{"order_id":"{{{LinkOrderId}}}"}},
              {"event_details":{"event_type":"DISPUTE"},"order_details":{"order_id":"../../x"}}]}
            """);
        _http.Respond(HttpStatusCode.OK, """
            [{"dispute_id":56,"dispute_type":"CHARGEBACK","dispute_status":"CHARGEBACK_CREATED","dispute_amount":10,"order_details":{"cf_payment_id":1489901523}}]
            """);

        var disputes = await Gateway.GetDisputesAsync(Now.AddDays(-30), Settings, Secrets, Ct);

        disputes.ShouldHaveSingleItem().DisputeId.ShouldBe("56");
        var events = _http.Requests[0];
        events.Uri.PathAndQuery.ShouldBe("/pg/recon");
        events.Json().GetProperty("filters").GetProperty("start_date").GetString().ShouldBe("2026-09-22T10:00:00+05:30");
        _http.Requests.Select(r => r.Uri.PathAndQuery).ShouldBe(["/pg/recon", $"/pg/orders/{LinkOrderId}/disputes"]);
    }

    [Theory]
    [InlineData("seconds", true)]
    [InlineData("milliseconds", true)]
    [InlineData("tampered", false)]
    [InlineData("other-secret", false)]
    [InlineData("four-days-old", false)]
    [InlineData("ten-minutes-ahead", false)]
    [InlineData("no-timestamp", false)]
    [InlineData("not-a-number", false)]
    public void Checks_a_webhooks_signature_over_its_timestamp_and_body(string delivery, bool accepted)
    {
        const string body = """{"type":"PAYMENT_SUCCESS_WEBHOOK","data":{"order":{"order_id":"CFPay_1"},"payment":{"cf_payment_id":12376123}}}""";
        var signedAt = delivery switch
        {
            "four-days-old" => Now.AddDays(-4),
            "ten-minutes-ahead" => Now.AddMinutes(10),
            _ => Now.AddSeconds(-30),
        };
        var timestamp = delivery switch
        {
            "milliseconds" => signedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "no-timestamp" => null,
            "not-a-number" => "2026-09-25T10:00:00Z",
            _ => signedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var signature = CashfreeSignatures.Sign(delivery == "other-secret" ? "someone-elses-secret" : "cfsk_ma_test_secret", timestamp ?? string.Empty, body);
        var received = delivery == "tampered" ? body.Replace("12376123", "99999999", StringComparison.Ordinal) : body;

        Gateway.VerifyWebhook(received, signature, timestamp, Secrets, Now).ShouldBe(accepted);
    }

    [Fact]
    public void Has_no_browser_checkout_signature_to_trust()
    {
        Gateway.VerifySignature("hcl_0199f0a1b2c3d4e5f60718293a4b|12376123", "anything", Secrets).ShouldBeFalse();
    }

    private void Link(string status, string expiry) =>
        _http.Respond(HttpStatusCode.OK, $$$"""
            {"cf_link_id":30111525,"link_id":"{{{LinkId}}}","link_status":"{{{status}}}","link_amount":1500.5,"link_amount_paid":0,
             "link_url":"https://payments.cashfree.com/links/z64lc99v754g","link_expiry_time":"{{{expiry}}}"}
            """);

    private void LinkOrders(params (string Status, string Id)[] orders) =>
        _http.Respond(HttpStatusCode.OK, "[" + string.Join(",", orders.Select(o =>
            $$$"""{"cf_order_id":2149460581,"link_id":"{{{LinkId}}}","order_id":"{{{o.Id}}}","order_status":"{{{o.Status}}}","order_amount":1500.5,"created_at":"2026-09-25T09:40:00+05:30"}""")) + "]");

    private void PaidOrderOfLink()
    {
        LinkOrders(("PAID", LinkOrderId));
        _http.Respond(HttpStatusCode.OK, $$$"""[{"cf_payment_id":12376123,"order_id":"{{{LinkOrderId}}}","payment_status":"SUCCESS","payment_amount":1500.5}]""");
    }

    /// <summary>The Node guide's refund sample, with HC360's key as its refund id.</summary>
    private static string Refund(string refundId, string status) => $$$"""
        {"cf_payment_id":2388816360,"cf_refund_id":"refund_40564529","created_at":"2026-09-25T10:58:06+05:30","entity":"refund","metadata":null,
         "order_id":"{{{LinkOrderId}}}","processed_at":null,"refund_amount":500,"refund_arn":{{{(status == "SUCCESS" ? "\"RF12312\"" : "null")}}},"refund_charge":0,
         "refund_currency":"INR","refund_id":"{{{refundId}}}","refund_mode":null,"refund_note":"Bill cancelled",
         "refund_speed":{"requested":"STANDARD","accepted":"STANDARD","processed":null,"message":null},"refund_splits":[],
         "refund_status":"{{{status}}}","refund_type":"MERCHANT_INITIATED","status_description":"In Progress"}
        """;
}
