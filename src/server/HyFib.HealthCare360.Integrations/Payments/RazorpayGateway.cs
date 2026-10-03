using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// Razorpay: orders (hosted Checkout), payment links (sent by HC360 over SMS/WhatsApp), payment status and refunds,
/// with HTTP basic auth (key id / key secret). Amounts travel in paise. Customer details are sent to Razorpay but
/// never written to the integration log.
/// </summary>
internal sealed partial class RazorpayGateway(HttpClient http, IntegrationTrace trace, TimeProvider time) : IPaymentProvider
{
    public string Key => ProviderKeys.Razorpay;

    public async Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var (baseUri, keyId, keySecret) = Credentials(settings, secrets);
        var paise = PaymentAmounts.ToPaise(request.Amount);
        var currency = Currency(request.Currency);
        var payload = new { amount = paise, currency, receipt = IntegrationTrace.Truncate(request.Receipt, 40), notes = Notes(request.Notes) };
        var summary = $"{{\"amount\":{paise},\"currency\":\"{currency}\",\"receipt\":\"{request.Receipt}\"}}";

        using var document = await SendAsync(HttpMethod.Post, IntegrationHttp.Combine(baseUri, "v1/orders"), payload, keyId, keySecret,
            "order.create", request.Receipt, summary, cancellationToken);
        var orderId = IntegrationHttp.Property(document.RootElement, "id") ?? throw new IntegrationException("Razorpay did not return an order id.");
        return new PaymentOrder(Key, orderId, request.Amount, currency, keyId, null);
    }

    public async Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var (baseUri, keyId, keySecret) = Credentials(settings, secrets);
        var paise = PaymentAmounts.ToPaise(request.Amount);
        var currency = Currency(request.Currency);
        var callback = IntegrationHttp.TryCreateEndpoint(settings.PaymentLinkCallbackUrl, requireHttps: true, out var callbackUri) ? callbackUri.AbsoluteUri : null;
        var payload = new
        {
            amount = paise,
            currency,
            accept_partial = false,
            reference_id = IntegrationTrace.Truncate(request.Receipt, 40),
            description = IntegrationTrace.Truncate(request.Description, 2048),
            customer = new
            {
                name = IntegrationTrace.Truncate(request.CustomerName, 100),
                contact = Phone.Normalize(request.CustomerPhone),
                email = string.IsNullOrWhiteSpace(request.CustomerEmail) ? null : request.CustomerEmail,
            },
            notify = new { sms = settings.NotifyByRazorpay, email = settings.NotifyByRazorpay },
            reminder_enable = settings.NotifyByRazorpay,
            notes = Notes(request.Notes),
            expire_by = time.GetUtcNow().AddHours(Math.Clamp(settings.PaymentLinkExpiryHours, 1, 24 * 180)).ToUnixTimeSeconds(),
            callback_url = callback,
            callback_method = callback is null ? null : "get",
        };
        var summary = $"{{\"amount\":{paise},\"currency\":\"{currency}\",\"reference\":\"{request.Receipt}\"}}";

        using var document = await SendAsync(HttpMethod.Post, IntegrationHttp.Combine(baseUri, "v1/payment_links"), payload, keyId, keySecret,
            "link.create", request.Receipt, summary, cancellationToken);
        var linkId = IntegrationHttp.Property(document.RootElement, "id") ?? throw new IntegrationException("Razorpay did not return a payment link id.");
        return new PaymentOrder(Key, linkId, request.Amount, currency, keyId, IntegrationHttp.Property(document.RootElement, "short_url"));
    }

    public async Task<PaymentStatusResult> GetStatusAsync(string orderId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        if (!OrderIdPattern().IsMatch(orderId))
        {
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Failed, null, null, null, "Not a Razorpay order or payment link id.");
        }

        var (baseUri, keyId, keySecret) = Credentials(settings, secrets);
        if (orderId.StartsWith("plink_", StringComparison.Ordinal))
        {
            using var link = await SendAsync(HttpMethod.Get, IntegrationHttp.Combine(baseUri, $"v1/payment_links/{orderId}"), null, keyId, keySecret,
                "link.status", orderId, null, cancellationToken);
            var payments = link.RootElement.TryGetProperty("payments", out var array) ? RazorpayStatus.ReadPayments(array) : [];
            return RazorpayStatus.FromPaymentLink(orderId, IntegrationHttp.Property(link.RootElement, "status"), payments);
        }

        using var document = await SendAsync(HttpMethod.Get, IntegrationHttp.Combine(baseUri, $"v1/orders/{orderId}/payments"), null, keyId, keySecret,
            "order.status", orderId, null, cancellationToken);
        var items = document.RootElement.TryGetProperty("items", out var itemArray) ? RazorpayStatus.ReadPayments(itemArray) : [];
        return RazorpayStatus.FromPayments(orderId, items);
    }

    /// <summary>
    /// <c>POST v1/payments/{id}/refund</c>. The idempotency key goes in <c>X-Refund-Idempotency</c>, so Razorpay
    /// answers a repeated request with the refund it already made, and in the refund's <c>receipt</c>.
    /// </summary>
    public async Task<RefundResult> RefundAsync(
        string paymentId, decimal amount, string reason, string? idempotencyKey, PaymentSettings settings,
        PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        if (!PaymentIdPattern().IsMatch(paymentId))
        {
            return new RefundResult(false, null, "Not a Razorpay payment id.");
        }

        if (idempotencyKey is not null && !IdempotencyKeyPattern().IsMatch(idempotencyKey))
        {
            return new RefundResult(false, null, "Not a usable idempotency key.");
        }

        try
        {
            var (baseUri, keyId, keySecret) = Credentials(settings, secrets);
            var paise = PaymentAmounts.ToPaise(amount);
            var payload = new
            {
                amount = paise,
                speed = "normal",
                receipt = idempotencyKey,
                notes = new Dictionary<string, string>
                {
                    ["reason"] = IntegrationTrace.Truncate(reason, 256) ?? string.Empty,
                },
            };
            using var document = await SendAsync(HttpMethod.Post, IntegrationHttp.Combine(baseUri, $"v1/payments/{paymentId}/refund"), payload, keyId, keySecret,
                "refund", paymentId, $"{{\"amount\":{paise}}}", cancellationToken, idempotencyKey);
            return new RefundResult(true, IntegrationHttp.Property(document.RootElement, "id"), null);
        }
        catch (IntegrationException ex)
        {
            return new RefundResult(false, null, ex.Message);
        }
    }

    public bool VerifySignature(string payload, string signature, PaymentSecrets secrets) =>
        RazorpaySignatures.Verify(payload, signature, secrets.KeySecret, secrets.WebhookSecret);

    /// <summary>
    /// The settlements of one day: every page of <c>GET v1/settlements/recon/combined?year&amp;month&amp;day</c>, grouped
    /// by settlement, each completed with its header from <c>GET v1/settlements/{id}</c> (status, amount and UTR). All
    /// or nothing: a failed page fails the day, so a half-read settlement is never stored.
    /// </summary>
    public async Task<IReadOnlyList<PaymentSettlement>> GetSettlementsAsync(
        DateOnly settledOn, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var (baseUri, keyId, keySecret) = Credentials(settings, secrets);
        var day = settledOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var items = new List<RazorpayReconItem>();
        for (var page = 0; ; page++)
        {
            if (page >= RazorpaySettlements.MaxPages)
            {
                throw new IntegrationException($"Razorpay listed more than {RazorpaySettlements.MaxPages * RazorpaySettlements.PageSize} transactions for {day}.");
            }

            var query = string.Create(CultureInfo.InvariantCulture,
                $"v1/settlements/recon/combined?year={settledOn.Year}&month={settledOn.Month:00}&day={settledOn.Day:00}&count={RazorpaySettlements.PageSize}&skip={page * RazorpaySettlements.PageSize}");
            using var document = await SendAsync(HttpMethod.Get, IntegrationHttp.Combine(baseUri, query), null, keyId, keySecret,
                "settlement.recon", day, $"{{\"day\":\"{day}\",\"page\":{page + 1}}}", cancellationToken);
            items.AddRange(RazorpaySettlements.ReadReconPage(document.RootElement));
            if (RazorpaySettlements.CountItems(document.RootElement) < RazorpaySettlements.PageSize)
            {
                break;
            }
        }

        var settlements = new List<PaymentSettlement>();
        foreach (var group in items.GroupBy(i => i.SettlementId))
        {
            using var header = await SendAsync(HttpMethod.Get, IntegrationHttp.Combine(baseUri, $"v1/settlements/{group.Key}"), null, keyId, keySecret,
                "settlement.fetch", group.Key, null, cancellationToken);
            settlements.Add(RazorpaySettlements.Build(header.RootElement, group.Key, group.ToList()));
        }

        return settlements;
    }

    /// <summary><c>GET v1/refunds/{id}</c>: pending, processed or failed, with the bank's reference (ARN, RRN or UTR) once known.</summary>
    public async Task<RefundStatusResult> GetRefundStatusAsync(string refundId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        if (!RefundIdPattern().IsMatch(refundId))
        {
            return new RefundStatusResult(refundId, PaymentRefundStatus.Pending, null, null, null, null, "Not a Razorpay refund id.");
        }

        var (baseUri, keyId, keySecret) = Credentials(settings, secrets);
        using var document = await SendAsync(HttpMethod.Get, IntegrationHttp.Combine(baseUri, $"v1/refunds/{refundId}"), null, keyId, keySecret,
            "refund.status", refundId, null, cancellationToken);
        var root = document.RootElement;
        var status = IntegrationHttp.Property(root, "status") switch
        {
            "processed" => PaymentRefundStatus.Processed,
            "failed" => PaymentRefundStatus.Failed,
            _ => PaymentRefundStatus.Pending,
        };
        var bankReference = root.TryGetProperty("acquirer_data", out var acquirer)
            ? IntegrationHttp.Property(acquirer, "arn") ?? IntegrationHttp.Property(acquirer, "rrn") ?? IntegrationHttp.Property(acquirer, "utr")
            : null;
        return new RefundStatusResult(
            refundId,
            status,
            root.TryGetProperty("amount", out _) ? PaymentAmounts.FromPaise(RazorpayStatus.ReadLong(root, "amount")) : null,
            IntegrationHttp.Property(root, "payment_id") is { } paymentId && PaymentIdPattern().IsMatch(paymentId) ? paymentId : null,
            bankReference is { Length: > 0 and <= 64 } && bankReference.All(char.IsAsciiLetterOrDigit) ? bankReference : null,
            RazorpayStatus.ReadTime(root, "created_at"),
            status == PaymentRefundStatus.Failed ? "Razorpay could not return the money to the patient's account." : null);
    }

    /// <summary>Razorpay errors: <c>{"error":{"code":"BAD_REQUEST_ERROR","description":"…","field":"amount"}}</c>.</summary>
    internal static string ErrorMessage(string body, int status)
    {
        if (IntegrationHttp.TryParseJson(body, out var document))
        {
            using (document)
            {
                if (IntegrationHttp.ReadPath(document.RootElement, "error.description") is { Length: > 0 } description)
                {
                    return "Razorpay: " + description;
                }
            }
        }

        return $"Razorpay returned HTTP {status}.";
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, Uri uri, object? payload, string keyId, string keySecret, string operation,
        string? reference, string? summary, CancellationToken cancellationToken, string? refundIdempotencyKey = null)
    {
        var started = trace.Start();
        using var request = new HttpRequestMessage(method, uri);
        if (payload is not null)
        {
            request.Content = IntegrationHttp.JsonBody(payload);
        }

        if (refundIdempotencyKey is not null)
        {
            request.Headers.Add("X-Refund-Idempotency", refundIdempotencyKey);
        }

        IntegrationHttp.SetBasicAuth(request, keyId, keySecret);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || !IntegrationHttp.TryParseJson(body, out var document))
            {
                var error = response.IsSuccessStatusCode ? "Razorpay returned an unreadable response." : ErrorMessage(body, (int)response.StatusCode);
                await trace.LogAsync(IntegrationNames.Payment, Key, operation, false, started, reference, summary, $"HTTP {(int)response.StatusCode}", error);
                throw new IntegrationException(error);
            }

            await trace.LogAsync(IntegrationNames.Payment, Key, operation, true, started, reference, summary,
                $"HTTP {(int)response.StatusCode} id={IntegrationHttp.Property(document.RootElement, "id")} status={IntegrationHttp.Property(document.RootElement, "status")}");
            return document;
        }
        catch (Exception ex) when (ex is not IntegrationException && IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "Razorpay");
            await trace.LogAsync(IntegrationNames.Payment, Key, operation, false, started, reference, summary, error: error);
            throw new IntegrationException(error, ex);
        }
    }

    private static (Uri BaseUri, string KeyId, string KeySecret) Credentials(PaymentSettings settings, PaymentSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(settings.KeyId) || string.IsNullOrWhiteSpace(secrets.KeySecret))
        {
            throw new IntegrationException("The Razorpay key id and key secret are not configured.");
        }

        if (!IntegrationHttp.TryCreateEndpoint(settings.BaseUrl, requireHttps: true, out var baseUri))
        {
            throw new IntegrationException("The Razorpay base URL must be an https address.");
        }

        return (baseUri, settings.KeyId.Trim(), secrets.KeySecret);
    }

    private static string Currency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "INR" : currency.Trim().ToUpperInvariant() is { Length: 3 } code ? code : "INR";

    /// <summary>Razorpay accepts at most 15 notes of 256 characters.</summary>
    private static Dictionary<string, string>? Notes(IReadOnlyDictionary<string, string>? notes) =>
        notes is null || notes.Count == 0
            ? null
            : notes.Take(15).ToDictionary(n => IntegrationTrace.Truncate(n.Key, 40)!, n => IntegrationTrace.Truncate(n.Value, 256) ?? string.Empty);

    [GeneratedRegex("^(order|plink)_[A-Za-z0-9]{6,40}$")]
    private static partial Regex OrderIdPattern();

    [GeneratedRegex("^pay_[A-Za-z0-9]{6,40}$")]
    private static partial Regex PaymentIdPattern();

    [GeneratedRegex("^[A-Za-z0-9]{1,40}$")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex("^rfnd_[A-Za-z0-9]{6,40}$")]
    private static partial Regex RefundIdPattern();
}
