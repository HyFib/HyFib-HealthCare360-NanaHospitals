using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// Cashfree Payments, from its official SDKs (cashfree-pg Node 6.0.6 and Python 6.0.1, API version
/// <see cref="ApiVersion"/>). HC360 takes every Cashfree payment through a payment link it names (<c>hcl_…</c>): sent to
/// the patient by SMS or WhatsApp, or opened by the patient portal as Cashfree's own hosted page, so no Cashfree script
/// runs in HC360's pages and nothing in the browser is trusted. Status, refunds, settlements and disputes are read from
/// the API with the client id and secret (<c>x-client-id</c>, <c>x-client-secret</c>); amounts travel in rupees.
/// Customer details are sent to Cashfree but never written to the integration log.
/// </summary>
internal sealed partial class CashfreeGateway(HttpClient http, IntegrationTrace trace, TimeProvider time) : IPaymentProvider
{
    public const string ProductionBaseUrl = "https://api.cashfree.com/pg";

    public const string SandboxBaseUrl = "https://sandbox.cashfree.com/pg";

    /// <summary>The API version the SDKs send by default (<c>x-api-version</c>); the shapes read here are this version's.</summary>
    public const string ApiVersion = "2026-01-01";

    /// <summary>Cashfree lists at most 1,000 settlements or events per call.</summary>
    public const int PageSize = 1000;

    /// <summary>More pages than this for one read is refused rather than half read.</summary>
    public const int MaxPages = 50;

    /// <summary>At most this many of a link's orders are read (each patient attempt on a link is an order).</summary>
    public const int MaxLinkOrders = 10;

    /// <summary>
    /// Cashfree has no list of disputes: the hourly read pages the account's events of the last days for disputes and
    /// chargebacks. A dispute raised before reached HC360 by its webhook, its settlement or an earlier run, and each open
    /// case is read again by its id.
    /// </summary>
    public static readonly TimeSpan DisputeLookBack = TimeSpan.FromDays(3);

    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public string Key => ProviderKeys.Cashfree;

    public string Name => "Cashfree";

    public bool IsSetUp(PaymentSettings settings, PaymentSecrets secrets) =>
        !string.IsNullOrWhiteSpace(settings.CashfreeClientId) && !string.IsNullOrWhiteSpace(secrets.CashfreeClientSecret);

    /// <summary>The portal's checkout is Cashfree's hosted page for a payment link (the SDKs document no in-page checkout).</summary>
    public Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken) =>
        CreateLinkAsync(request, settings, secrets, "order.create", cancellationToken);

    public Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken) =>
        CreateLinkAsync(request, settings, secrets, "link.create", cancellationToken);

    /// <summary>
    /// <c>GET /links/{link_id}</c> and the link's orders with their payments: a successful payment is the link's,
    /// whatever the link says; otherwise a cancelled or expired link, a payment the bank has not finished, or the last
    /// failed attempt (the link stays open for another).
    /// </summary>
    public async Task<PaymentStatusResult> GetStatusAsync(string orderId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        if (!LinkIdPattern().IsMatch(orderId))
        {
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Failed, null, null, null, "Not a Cashfree payment link made by HC360.");
        }

        var api = Credentials(settings, secrets);
        using var link = await SendAsync(api, HttpMethod.Get, $"links/{orderId}", null, "link.status", orderId, null, cancellationToken);
        var payments = await LinkPaymentsAsync(api, orderId, paidOnly: false, cancellationToken);
        return CashfreeReading.LinkStatus(
            orderId, CashfreeReading.Text(link.RootElement, "link_status"), CashfreeReading.Time(link.RootElement, "link_expiry_time"),
            time.GetUtcNow(), payments);
    }

    /// <summary>
    /// <c>POST /orders/{order_id}/refunds</c> on the link's order that holds the payment. HC360's key is the refund's own
    /// id (<c>refund_id</c>) and, as a UUID, its <c>x-idempotency-key</c>; when Cashfree answers 409 (it has that
    /// refund already), the refund it holds is read and returned, so a request sent twice never refunds twice.
    /// </summary>
    public async Task<RefundResult> RefundAsync(
        GatewayPaymentRef payment, decimal amount, string reason, string? idempotencyKey, PaymentSettings settings,
        PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        if (CashfreeReading.SafeId(payment.PaymentId) is null)
        {
            return new RefundResult(false, null, "Not a Cashfree payment id.");
        }

        if (payment.OrderId is null || !LinkIdPattern().IsMatch(payment.OrderId))
        {
            return new RefundResult(false, null, "The Cashfree payment link this payment paid is not known, so the refund cannot be sent. Refund it by another mode.");
        }

        if (idempotencyKey is not null && !RefundIdPattern().IsMatch(idempotencyKey))
        {
            return new RefundResult(false, null, "Not a usable idempotency key.");
        }

        try
        {
            var api = Credentials(settings, secrets);
            var rupees = Rupees(amount);
            var refundId = idempotencyKey ?? "hcr" + Guid.CreateVersion7().ToString("N");
            var order = await OrderOfPaymentAsync(api, payment.OrderId, payment.PaymentId, cancellationToken);
            var payload = new
            {
                refund_amount = rupees,
                refund_id = refundId,
                refund_note = Note(reason),
                refund_speed = "STANDARD",
            };
            var (status, document) = await TrySendAsync(api, HttpMethod.Post, $"orders/{Uri.EscapeDataString(order)}/refunds", payload, "refund",
                payment.PaymentId, $"{{\"amount\":{rupees.ToString(CultureInfo.InvariantCulture)}}}", cancellationToken, IdempotencyUuid(refundId));
            using (document)
            {
                if (status == HttpStatusCode.Conflict)
                {
                    // Cashfree has this refund already (an earlier attempt that timed out): it is the one to follow.
                    using var existing = await SendAsync(api, HttpMethod.Get, $"orders/{Uri.EscapeDataString(order)}/refunds/{refundId}", null,
                        "refund.existing", payment.PaymentId, null, cancellationToken);
                    return new RefundResult(true, CashfreeReading.SafeId(CashfreeReading.Text(existing.RootElement, "refund_id")) ?? refundId, null);
                }

                return new RefundResult(true, CashfreeReading.SafeId(CashfreeReading.Text(document!.RootElement, "refund_id")) ?? refundId, null);
            }
        }
        catch (IntegrationException ex)
        {
            return new RefundResult(false, null, ex.Message);
        }
    }

    /// <summary>Cashfree confirms a payment server to server; its checkout returns nothing to verify in the browser.</summary>
    public bool VerifySignature(string payload, string signature, PaymentSecrets secrets) => false;

    public bool VerifyWebhook(string payload, string signature, string? timestamp, PaymentSecrets secrets, DateTimeOffset now) =>
        CashfreeSignatures.Verify(secrets.CashfreeClientSecret, payload, signature, timestamp, now);

    /// <summary>
    /// The settlements paid to the bank on an Indian day: every page of <c>POST /settlements</c> around the day, those
    /// processed that day (or initiated, while not yet processed), each with its events from every page of
    /// <c>POST /settlement/recon</c>. All or nothing: a failed page fails the day, so a half-read settlement is never stored.
    /// </summary>
    public async Task<IReadOnlyList<PaymentSettlement>> GetSettlementsAsync(
        DateOnly settledOn, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var api = Credentials(settings, secrets);
        var from = new DateTimeOffset(settledOn.AddDays(-1).ToDateTime(TimeOnly.MinValue), India);
        var to = new DateTimeOffset(settledOn.AddDays(2).ToDateTime(TimeOnly.MinValue), India).AddSeconds(-1);
        var headers = new List<JsonElement>();
        var documents = new List<JsonDocument>();
        try
        {
            string? cursor = null;
            for (var page = 0; ; page++)
            {
                if (page >= MaxPages)
                {
                    throw new IntegrationException($"Cashfree listed more than {MaxPages * PageSize} settlements around {settledOn:dd-MM-yyyy}; they were not read.");
                }

                var payload = new
                {
                    pagination = new { limit = PageSize, cursor },
                    filters = new { start_date = Iso(from), end_date = Iso(to) },
                };
                var document = await SendAsync(api, HttpMethod.Post, "settlements", payload, "settlements", settledOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, cancellationToken);
                documents.Add(document);
                var data = CashfreeReading.Child(document.RootElement, "data");
                if (data.ValueKind == JsonValueKind.Array)
                {
                    headers.AddRange(data.EnumerateArray());
                }

                if ((cursor = CashfreeReading.NextCursor(document.RootElement, PageSize)) is null)
                {
                    break;
                }
            }

            var result = new List<PaymentSettlement>();
            foreach (var header in headers.Select(CashfreeReading.Settlement).OfType<(string Id, string? Utr, PaymentSettlementStatus Status, decimal Amount, decimal Fees, decimal Tax, DateTimeOffset? At)>()
                         .Where(h => h.At is { } at && DateOnly.FromDateTime(at.ToOffset(India).DateTime) == settledOn)
                         .DistinctBy(h => h.Id))
            {
                var items = await SettlementEventsAsync(api, header.Id, header.Utr, cancellationToken);
                result.Add(new PaymentSettlement(header.Id, header.Utr, header.Status, header.Amount, header.Fees, header.Tax, header.At!.Value, items));
            }

            return result;
        }
        finally
        {
            foreach (var document in documents)
            {
                document.Dispose();
            }
        }
    }

    public async Task<RefundStatusResult> GetRefundStatusAsync(
        string refundId, GatewayPaymentRef payment, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        if (!RefundIdPattern().IsMatch(refundId) || CashfreeReading.SafeId(payment.PaymentId) is null
            || payment.OrderId is null || !LinkIdPattern().IsMatch(payment.OrderId))
        {
            return new RefundStatusResult(refundId, PaymentRefundStatus.Pending, null, null, null, null, "Not a Cashfree refund HC360 can look up.");
        }

        var api = Credentials(settings, secrets);
        var order = await OrderOfPaymentAsync(api, payment.OrderId, payment.PaymentId, cancellationToken);
        using var document = await SendAsync(api, HttpMethod.Get, $"orders/{Uri.EscapeDataString(order)}/refunds/{refundId}", null, "refund.status",
            refundId, null, cancellationToken);
        return CashfreeReading.Refund(refundId, document.RootElement);
    }

    /// <summary><c>GET /disputes/{dispute_id}</c>: Cashfree's dispute ids are numbers, and nothing else goes into the URL.</summary>
    public async Task<GatewayDispute> GetDisputeAsync(string disputeId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        if (!CashfreeReading.IsDisputeId(disputeId))
        {
            throw new IntegrationException("Not a Cashfree dispute id.");
        }

        var api = Credentials(settings, secrets);
        using var document = await SendAsync(api, HttpMethod.Get, $"disputes/{disputeId}", null, "dispute", disputeId, null, cancellationToken);
        return CashfreeReading.Dispute(document.RootElement) ?? throw new IntegrationException($"Cashfree's dispute {disputeId} could not be read.");
    }

    /// <summary>
    /// Cashfree lists no disputes by date: every page of the account's events (<c>POST /recon</c>) since
    /// <paramref name="since"/>, at most <see cref="DisputeLookBack"/> back, names the orders that had a dispute or a
    /// chargeback, and each order's disputes are read (<c>GET /orders/{order_id}/disputes</c>). All or nothing.
    /// </summary>
    public async Task<IReadOnlyList<GatewayDispute>> GetDisputesAsync(
        DateTimeOffset since, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var api = Credentials(settings, secrets);
        var now = time.GetUtcNow();
        var from = since > now - DisputeLookBack ? since : now - DisputeLookBack;
        var orders = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
            {
                throw new IntegrationException($"Cashfree listed more than {MaxPages * PageSize} events since {from.ToOffset(India):dd-MM-yyyy HH:mm}; its disputes were not read.");
            }

            var payload = new
            {
                pagination = new { limit = PageSize, cursor },
                filters = new { start_date = Iso(from), end_date = Iso(now) },
            };
            using var document = await SendAsync(api, HttpMethod.Post, "recon", payload, "disputes.events", null, null, cancellationToken);
            var data = CashfreeReading.Child(document.RootElement, "data");
            foreach (var row in data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var type = CashfreeReading.Text(CashfreeReading.Child(row, "event_details"), "event_type");
                if (type is "DISPUTE" or "CHARGEBACK" or "DISPUTE_REVERSAL" or "CHARGEBACK_REVERSAL"
                    && CashfreeReading.SafeId(CashfreeReading.Text(CashfreeReading.Child(row, "order_details"), "order_id")) is { } order)
                {
                    orders.Add(order);
                }
            }

            if ((cursor = CashfreeReading.NextCursor(document.RootElement, PageSize)) is null)
            {
                break;
            }
        }

        var disputes = new List<GatewayDispute>();
        foreach (var order in orders)
        {
            using var document = await SendAsync(api, HttpMethod.Get, $"orders/{Uri.EscapeDataString(order)}/disputes", null, "disputes.order", order, null, cancellationToken);
            disputes.AddRange(CashfreeReading.Disputes(document.RootElement));
        }

        return disputes.DistinctBy(d => d.DisputeId).ToList();
    }

    private async Task<PaymentOrder> CreateLinkAsync(
        CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, string operation, CancellationToken cancellationToken)
    {
        var api = Credentials(settings, secrets);
        var rupees = Rupees(request.Amount);
        var currency = Currency(request.Currency);
        var phone = CashfreePhone(request.CustomerPhone)
            ?? throw new IntegrationException("Cashfree needs the patient's mobile number to make a payment link. Add it to the patient's record, or take the payment another way.");
        var linkId = "hcl_" + Guid.CreateVersion7().ToString("N")[..12] + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var callback = IntegrationHttp.TryCreateEndpoint(settings.PaymentLinkCallbackUrl, requireHttps: true, out var callbackUri)
            && callbackUri.AbsoluteUri.Length <= 250 ? callbackUri.AbsoluteUri : null;
        var email = string.IsNullOrWhiteSpace(request.CustomerEmail) ? null : IntegrationTrace.Truncate(request.CustomerEmail.Trim(), 100);
        var payload = new
        {
            link_id = linkId,
            link_amount = rupees,
            link_currency = currency,
            link_purpose = IntegrationTrace.Truncate(request.Description, 500),
            customer_details = new
            {
                customer_phone = phone,
                customer_name = CustomerName(request.CustomerName),
                customer_email = email,
            },
            link_partial_payments = false,
            link_expiry_time = Iso(time.GetUtcNow().AddHours(Math.Clamp(settings.PaymentLinkExpiryHours, 1, 24 * 90))),
            link_notify = new { send_sms = settings.NotifyByRazorpay, send_email = settings.NotifyByRazorpay && email is not null },
            link_auto_reminders = settings.NotifyByRazorpay,
            link_notes = Notes(request),
            link_meta = callback is null ? null : new { return_url = callback },
        };
        var summary = $"{{\"amount\":{rupees.ToString(CultureInfo.InvariantCulture)},\"currency\":\"{currency}\",\"reference\":\"{request.Receipt}\"}}";
        using var document = await SendAsync(api, HttpMethod.Post, "links", payload, operation, request.Receipt, summary, cancellationToken);
        if (CashfreeReading.Text(document.RootElement, "link_id") is { } returned && returned != linkId)
        {
            throw new IntegrationException("Cashfree answered with another payment link than the one HC360 asked for.");
        }

        var url = CashfreeReading.Text(document.RootElement, "link_url");
        if (!IntegrationHttp.TryCreateEndpoint(url, requireHttps: true, out var linkUri))
        {
            throw new IntegrationException("Cashfree did not return the payment link's https address.");
        }

        return new PaymentOrder(Key, linkId, request.Amount, currency, null, linkUri.AbsoluteUri);
    }

    /// <summary>The payments of a link's orders (paid ones only, or every attempt), newest orders first.</summary>
    private async Task<IReadOnlyList<CashfreePayment>> LinkPaymentsAsync(Api api, string linkId, bool paidOnly, CancellationToken cancellationToken)
    {
        using var orders = await SendAsync(api, HttpMethod.Get, $"links/{linkId}/orders?status={(paidOnly ? "PAID" : "ALL")}", null, "link.orders",
            linkId, null, cancellationToken);
        var ids = orders.RootElement.ValueKind == JsonValueKind.Array
            ? orders.RootElement.EnumerateArray()
                .Select(o => (Id: CashfreeReading.SafeId(CashfreeReading.Text(o, "order_id")), Status: CashfreeReading.Text(o, "order_status"),
                    At: CashfreeReading.Time(o, "created_at")))
                .Where(o => o.Id is not null && (!paidOnly || o.Status == "PAID"))
                .OrderByDescending(o => o.Status == "PAID").ThenByDescending(o => o.At ?? DateTimeOffset.MinValue)
                .Take(MaxLinkOrders)
                .Select(o => o.Id!)
                .Distinct()
                .ToList()
            : [];
        var payments = new List<CashfreePayment>();
        foreach (var order in ids)
        {
            using var document = await SendAsync(api, HttpMethod.Get, $"orders/{Uri.EscapeDataString(order)}/payments", null, "order.payments",
                linkId, null, cancellationToken);
            payments.AddRange(CashfreeReading.Payments(document.RootElement));
        }

        return payments;
    }

    /// <summary>The link's order that holds the payment (Cashfree refunds by order).</summary>
    private async Task<string> OrderOfPaymentAsync(Api api, string linkId, string paymentId, CancellationToken cancellationToken)
    {
        var payments = await LinkPaymentsAsync(api, linkId, paidOnly: true, cancellationToken);
        return payments.FirstOrDefault(p => p.PaymentId == paymentId && p.Status == "SUCCESS")?.OrderId
            ?? throw new IntegrationException($"Cashfree has no successful payment {paymentId} on payment link {linkId}.");
    }

    /// <summary>Every page of a settlement's events; by its id when Cashfree's ids are numbers, else by its UTR.</summary>
    private async Task<IReadOnlyList<PaymentSettlementItem>> SettlementEventsAsync(Api api, string settlementId, string? utr, CancellationToken cancellationToken)
    {
        object filters = long.TryParse(settlementId, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
            ? new { cf_settlement_ids = new[] { numeric } }
            : utr is not null
                ? new { settlement_utrs = new[] { utr } }
                : throw new IntegrationException($"Cashfree's settlement {settlementId} has neither a numeric id nor a UTR to read its transactions by.");
        var items = new List<PaymentSettlementItem>();
        string? cursor = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
            {
                throw new IntegrationException($"Cashfree's settlement {settlementId} lists more than {MaxPages * PageSize} transactions; it was not read.");
            }

            var payload = new { pagination = new { limit = PageSize, cursor }, filters };
            using var document = await SendAsync(api, HttpMethod.Post, "settlement/recon", payload, "settlement.recon", settlementId, null, cancellationToken);
            var data = CashfreeReading.Child(document.RootElement, "data");
            if (data.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(data.EnumerateArray().Select(CashfreeReading.Event).OfType<PaymentSettlementItem>());
            }

            if ((cursor = CashfreeReading.NextCursor(document.RootElement, PageSize)) is null)
            {
                break;
            }
        }

        return items.DistinctBy(i => (i.EntityId, i.Type)).ToList();
    }

    private async Task<JsonDocument> SendAsync(
        Api api, HttpMethod method, string path, object? payload, string operation, string? reference, string? summary,
        CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var (status, document) = await TrySendAsync(api, method, path, payload, operation, reference, summary, cancellationToken, idempotencyKey);
        if (status == HttpStatusCode.Conflict)
        {
            document?.Dispose();
            throw new IntegrationException("Cashfree: this was already done (HTTP 409).");
        }

        return document!;
    }

    /// <summary>
    /// One call; a 409 (Cashfree holds the resource already) is returned to the caller, every other failure throws. The
    /// body is never logged: Cashfree echoes the customer's name, phone and e-mail, and card or bank details.
    /// </summary>
    private async Task<(HttpStatusCode Status, JsonDocument? Document)> TrySendAsync(
        Api api, HttpMethod method, string path, object? payload, string operation, string? reference, string? summary,
        CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var started = trace.Start();
        using var request = new HttpRequestMessage(method, IntegrationHttp.Combine(api.BaseUri, path));
        if (payload is not null)
        {
            request.Content = IntegrationHttp.JsonBody(payload);
        }

        request.Headers.Add("x-client-id", api.ClientId);
        request.Headers.Add("x-client-secret", api.ClientSecret);
        request.Headers.Add("x-api-version", ApiVersion);
        request.Headers.Add("x-request-id", Guid.CreateVersion7().ToString());
        request.Headers.Accept.ParseAdd("application/json");
        if (idempotencyKey is not null)
        {
            request.Headers.Add("x-idempotency-key", idempotencyKey);
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                await trace.LogAsync(IntegrationNames.Payment, Key, operation, false, started, reference, summary, "HTTP 409", "Already done at Cashfree.");
                return (HttpStatusCode.Conflict, null);
            }

            if (!response.IsSuccessStatusCode || !IntegrationHttp.TryParseJson(body, out var document))
            {
                var error = response.IsSuccessStatusCode ? "Cashfree returned an unreadable response." : ErrorMessage(body, (int)response.StatusCode);
                await trace.LogAsync(IntegrationNames.Payment, Key, operation, false, started, reference, summary, $"HTTP {(int)response.StatusCode}", error);
                throw new IntegrationException(error);
            }

            var root = document.RootElement;
            await trace.LogAsync(IntegrationNames.Payment, Key, operation, true, started, reference, summary,
                root.ValueKind == JsonValueKind.Array
                    ? $"HTTP {(int)response.StatusCode} items={root.GetArrayLength()}"
                    : $"HTTP {(int)response.StatusCode} status={IntegrationTrace.Truncate(CashfreeReading.Text(root, "link_status") ?? CashfreeReading.Text(root, "refund_status") ?? CashfreeReading.Text(root, "dispute_status"), 40)}");
            return (response.StatusCode, document);
        }
        catch (Exception ex) when (ex is not IntegrationException && IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "Cashfree");
            await trace.LogAsync(IntegrationNames.Payment, Key, operation, false, started, reference, summary, error: error);
            throw new IntegrationException(error, ex);
        }
    }

    /// <summary>Cashfree's error (<c>{"message","code","type"}</c>) in a line, or the status when the body says nothing usable.</summary>
    private static string ErrorMessage(string body, int status)
    {
        if (IntegrationHttp.TryParseJson(body, out var document))
        {
            using (document)
            {
                if (CashfreeReading.Text(document.RootElement, "message") is { Length: > 0 } message)
                {
                    return "Cashfree: " + IntegrationTrace.Truncate(message.ReplaceLineEndings(" "), 200);
                }
            }
        }

        return status switch
        {
            401 => "Cashfree refused the client id and secret (HTTP 401).",
            429 => "Cashfree is limiting HC360's calls (HTTP 429); it is asked again later.",
            _ => $"Cashfree returned HTTP {status}.",
        };
    }

    private sealed record Api(Uri BaseUri, string ClientId, string ClientSecret);

    private static Api Credentials(PaymentSettings settings, PaymentSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(settings.CashfreeClientId) || string.IsNullOrWhiteSpace(secrets.CashfreeClientSecret))
        {
            throw new IntegrationException("The Cashfree client id and client secret are not configured.");
        }

        if (!IntegrationHttp.TryCreateEndpoint(settings.CashfreeBaseUrl, requireHttps: true, out var baseUri))
        {
            throw new IntegrationException("The Cashfree address must be an https address.");
        }

        return new Api(baseUri, settings.CashfreeClientId.Trim(), secrets.CashfreeClientSecret);
    }

    /// <summary>Rupees with paise, after the same ₹1 to ₹1,00,00,000 check as every gateway call.</summary>
    private static decimal Rupees(decimal amount) => PaymentAmounts.FromPaise(PaymentAmounts.ToPaise(amount));

    private static string Currency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "INR" : currency.Trim().ToUpperInvariant() is { Length: 3 } code ? code : "INR";

    /// <summary>An Indian mobile as its ten digits; another country's number keeps its <c>+</c> (Cashfree's rule).</summary>
    private static string? CashfreePhone(string? phone) =>
        Phone.Normalize(phone) is { } e164
            ? e164.StartsWith("+91", StringComparison.Ordinal) && e164.Length == 13 ? e164[3..] : e164
            : null;

    /// <summary>Letters, digits, spaces, dots and hyphens only (Cashfree refuses special characters in a name), 3 to 100.</summary>
    private static string? CustomerName(string? name)
    {
        var cleaned = NameNoise().Replace(name ?? string.Empty, " ").Trim();
        cleaned = Spaces().Replace(cleaned, " ");
        return cleaned.Length < 3 ? null : IntegrationTrace.Truncate(cleaned, 100);
    }

    /// <summary>At most five notes; HC360's reference first, so the link can always be traced back.</summary>
    private static Dictionary<string, string> Notes(CreatePaymentOrder request)
    {
        var notes = new Dictionary<string, string> { ["hc360_reference"] = IntegrationTrace.Truncate(request.Receipt, 200) ?? string.Empty };
        foreach (var (key, value) in (request.Notes ?? new Dictionary<string, string>()).Take(4))
        {
            notes.TryAdd(IntegrationTrace.Truncate(key, 40)!, IntegrationTrace.Truncate(value, 200) ?? string.Empty);
        }

        return notes;
    }

    /// <summary>The refund's note: 3 to 100 characters.</summary>
    private static string Note(string reason)
    {
        var note = IntegrationTrace.Truncate(string.IsNullOrWhiteSpace(reason) ? "Refund" : reason.ReplaceLineEndings(" ").Trim(), 100)!;
        return note.Length < 3 ? "Refund" : note;
    }

    /// <summary>The same refund's <c>x-idempotency-key</c> on every attempt: a UUID made from its id.</summary>
    private static string IdempotencyUuid(string refundId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("hc360-cashfree-refund:" + refundId))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    /// <summary>ISO 8601 in Indian time, as Cashfree's samples write it.</summary>
    private static string Iso(DateTimeOffset at) => at.ToOffset(India).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>A link HC360 made: <c>hcl_</c> and 28 letters and digits.</summary>
    [GeneratedRegex("^hcl_[a-f0-9]{28}$")]
    private static partial Regex LinkIdPattern();

    /// <summary>Cashfree's refund id: 3 to 40 letters and digits.</summary>
    [GeneratedRegex("^[A-Za-z0-9]{3,40}$")]
    private static partial Regex RefundIdPattern();

    [GeneratedRegex("[^\\p{L}\\p{N} .-]")]
    private static partial Regex NameNoise();

    [GeneratedRegex("\\s{2,}")]
    private static partial Regex Spaces();
}
