using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Cashfree's API as its official SDKs describe it (x-api-version 2026-01-01), held in memory for the server under test:
/// payment links, the orders a patient's attempts make on them with their payments (ids as numbers, as in the SDK
/// guides' samples), and refunds by order that refuse a repeated <c>refund_id</c> with 409. Only the client id and secret
/// below are let in. Tests pay a link and settle a refund the way Cashfree would.
/// </summary>
public sealed class FakeCashfree
{
    public const string BaseUrl = "https://cashfree.test/pg";

    public const string ClientId = "TEST10182934a7c1f0";

    public const string ClientSecret = "cf-integration-test-secret";

    private readonly ConcurrentDictionary<string, JsonObject> _links = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<JsonObject>> _orders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonObject> _refunds = new(StringComparer.Ordinal);
    private long _payments = 5_114_911_100;

    /// <summary>Every call the server made, as <c>METHOD /pg/path?query</c>.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public HttpMessageHandler Handler() => new Forward(this);

    /// <summary>The patient pays the link in full: an order of the link, paid by a successful payment. Returns their ids.</summary>
    public (string OrderId, long PaymentId) Pay(string linkId)
    {
        var link = _links[linkId];
        var orderId = $"CFPay_{linkId[4..12]}_{Interlocked.Increment(ref _payments)}";
        var paymentId = Interlocked.Increment(ref _payments);
        var amount = link["link_amount"]!.GetValue<decimal>();
        var order = new JsonObject
        {
            ["cf_order_id"] = paymentId + 1,
            ["link_id"] = linkId,
            ["order_id"] = orderId,
            ["order_status"] = "PAID",
            ["order_amount"] = amount,
            ["created_at"] = Now(),
            ["payments"] = new JsonArray(new JsonObject
            {
                ["cf_payment_id"] = paymentId,
                ["order_id"] = orderId,
                ["payment_status"] = "SUCCESS",
                ["payment_amount"] = amount,
                ["payment_group"] = "upi",
                ["payment_time"] = Now(),
                ["payment_completion_time"] = Now(),
            }),
        };
        _orders.GetOrAdd(linkId, _ => []).Add(order);
        link["link_amount_paid"] = amount;
        link["link_status"] = "PAID";
        return (orderId, paymentId);
    }

    /// <summary>The bank finishes a refund (<c>SUCCESS</c> with its ARN, or <c>FAILED</c>).</summary>
    public void SettleRefund(string refundId, string status)
    {
        var refund = _refunds.Values.Single(r => (string)r["refund_id"]! == refundId);
        refund["refund_status"] = status;
        refund["refund_arn"] = status == "SUCCESS" ? "ARN" + refundId[..10].ToUpperInvariant() : null;
        refund["processed_at"] = status == "SUCCESS" ? Now() : null;
    }

    /// <summary>The refunds made on an order, as Cashfree holds them.</summary>
    public IReadOnlyList<JsonObject> RefundsOf(string orderId) =>
        _refunds.Where(r => r.Key.StartsWith(orderId + "/", StringComparison.Ordinal)).Select(r => r.Value).ToList();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        Calls.Enqueue($"{request.Method} {request.RequestUri.PathAndQuery}");
        if (Header(request, "x-client-id") != ClientId || Header(request, "x-client-secret") != ClientSecret
            || Header(request, "x-api-version") != "2026-01-01" || !path.StartsWith("/pg/", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Unauthorized, """{"message":"authentication Failed","code":"request_failed","type":"authentication_error"}""");
        }

        var parts = path["/pg/".Length..].Split('/');
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
        return (request.Method.Method, parts) switch
        {
            ("POST", ["links"]) => CreateLink(body!),
            ("GET", ["links", var id]) when _links.TryGetValue(id, out var link) => Json(HttpStatusCode.OK, link.ToJsonString()),
            ("GET", ["links", var id, "orders"]) => Json(HttpStatusCode.OK, new JsonArray(
                _orders.GetValueOrDefault(id, [])
                    .Where(o => request.RequestUri.Query.Contains("status=ALL", StringComparison.Ordinal) || (string)o["order_status"]! == "PAID")
                    .Select(o => (JsonNode)Without(o, "payments")).ToArray()).ToJsonString()),
            ("GET", ["orders", var orderId, "payments"]) when Order(orderId) is { } order =>
                Json(HttpStatusCode.OK, order["payments"]!.ToJsonString()),
            ("POST", ["orders", var orderId, "refunds"]) when Order(orderId) is { } order => Refund(orderId, order, body!),
            ("GET", ["orders", var orderId, "refunds", var refundId]) when _refunds.TryGetValue($"{orderId}/{refundId}", out var refund) =>
                Json(HttpStatusCode.OK, refund.ToJsonString()),
            _ => Json(HttpStatusCode.NotFound, """{"message":"Resource not found","code":"not_found","type":"invalid_request_error"}"""),
        };
    }

    private HttpResponseMessage CreateLink(JsonObject request)
    {
        var id = (string)request["link_id"]!;
        var link = new JsonObject
        {
            ["cf_link_id"] = Interlocked.Increment(ref _payments),
            ["link_id"] = id,
            ["link_status"] = "ACTIVE",
            ["link_currency"] = request["link_currency"]!.GetValue<string>(),
            ["link_amount"] = request["link_amount"]!.GetValue<decimal>(),
            ["link_amount_paid"] = 0,
            ["link_url"] = $"https://payments.cashfree.test/links/{id[4..16]}",
            ["link_expiry_time"] = request["link_expiry_time"]!.GetValue<string>(),
            ["customer_details"] = request["customer_details"]!.DeepClone(),
        };
        return _links.TryAdd(id, link)
            ? Json(HttpStatusCode.OK, link.ToJsonString())
            : Json(HttpStatusCode.Conflict, """{"message":"link_id already exists","code":"link_post_failed","type":"invalid_request_error"}""");
    }

    private HttpResponseMessage Refund(string orderId, JsonObject order, JsonObject request)
    {
        var refundId = (string)request["refund_id"]!;
        var refund = new JsonObject
        {
            ["cf_payment_id"] = order["payments"]![0]!["cf_payment_id"]!.GetValue<long>(),
            ["cf_refund_id"] = "refund_" + Interlocked.Increment(ref _payments),
            ["order_id"] = orderId,
            ["refund_id"] = refundId,
            ["entity"] = "refund",
            ["refund_amount"] = request["refund_amount"]!.GetValue<decimal>(),
            ["refund_currency"] = "INR",
            ["refund_note"] = request["refund_note"]?.GetValue<string>(),
            ["refund_status"] = "PENDING",
            ["refund_arn"] = null,
            ["status_description"] = "In Progress",
            ["created_at"] = Now(),
            ["processed_at"] = null,
        };
        return _refunds.TryAdd($"{orderId}/{refundId}", refund)
            ? Json(HttpStatusCode.OK, refund.ToJsonString())
            : Json(HttpStatusCode.Conflict, """{"message":"Resource already present","code":"refund_already_exists","type":"invalid_request_error"}""");
    }

    private JsonObject? Order(string orderId) =>
        _orders.Values.SelectMany(o => o).FirstOrDefault(o => (string)o["order_id"]! == orderId);

    private static JsonObject Without(JsonObject source, string name)
    {
        var copy = source.DeepClone().AsObject();
        copy.Remove(name);
        return copy;
    }

    private static string Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;

    private static string Now() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Forward(FakeCashfree cashfree) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cashfree.SendAsync(request, cancellationToken);
    }
}
