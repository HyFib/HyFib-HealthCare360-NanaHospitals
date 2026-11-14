using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>
/// Electronic payments: /api/v1/billing/pos/sales (card/UPI terminals, INT-POS), /api/v1/billing/payment-links
/// (INT-PG, sent by SMS/WhatsApp) and the anonymous, signature-verified gateway webhook.
/// </summary>
internal static class GatewayEndpoints
{
    /// <summary>Gateways sign webhooks in a provider-specific header; the first one present is verified.</summary>
    private static readonly string[] SignatureHeaders = ["X-Razorpay-Signature", "X-PayU-Signature", "X-Webhook-Signature", "X-Signature"];

    private const string RazorpayEventIdHeader = "X-Razorpay-Event-Id";

    private const int MaxWebhookChars = 64 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var pos = app.MapGroup("/api/v1/billing/pos/sales").WithTags("Billing - POS");
        pos.MapPost("/", StartSale).RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.Deposits).WithValidation<StartPosSaleRequest>();
        pos.MapGet("/{id:guid}", PollSale).RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.Deposits);
        pos.MapPost("/{id:guid}/cancel", CancelSale).RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.Deposits)
            .WithOptionalValidation<CancelPosSaleRequest>();

        var links = app.MapGroup("/api/v1/billing/payment-links").WithTags("Billing - Payment gateway");
        links.MapPost("/", CreateLink).RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.Deposits).WithValidation<CreatePaymentLinkRequest>();
        links.MapGet("/", ListLinks).RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.ChargesRead);
        links.MapPost("/{id:guid}/refresh", RefreshLink).RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.Deposits);

        app.MapPost("/api/v1/billing/webhooks/payment-gateway", Webhook)
            .WithTags("Billing - Payment gateway")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Partner);

        // Cashfree's deliveries come to their own address, so they are checked with Cashfree's account and read as
        // Cashfree writes them, whichever gateway the facility takes new payments through.
        app.MapPost("/api/v1/billing/webhooks/cashfree", CashfreeWebhook)
            .WithTags("Billing - Payment gateway")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Partner);
    }

    private static async Task<IResult> StartSale(StartPosSaleRequest request, PosService pos, CancellationToken ct) => (await pos.StartAsync(request, ct)).ToOk();

    private static async Task<IResult> PollSale(Guid id, PosService pos, CancellationToken ct) => (await pos.RefreshAsync(id, ct)).ToOk();

    private static async Task<IResult> CancelSale(Guid id, CancelPosSaleRequest? request, PosService pos, CancellationToken ct) =>
        (await pos.CancelAsync(id, request?.Note, ct)).ToOk();

    private static async Task<IResult> CreateLink(CreatePaymentLinkRequest request, PaymentGatewayService gateway, CancellationToken ct) =>
        (await gateway.CreateLinkAsync(request, ct)).ToCreated(o => $"/api/v1/billing/payment-links/{o.Id}");

    private static async Task<IResult> ListLinks(
        [AsParameters] PageQuery query, Guid? billId, Guid? ipAccountId, GatewayOrderStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var orders = db.Set<GatewayOrder>().AsNoTracking();
        if (billId is not null)
        {
            orders = orders.Where(o => o.BillId == billId);
        }

        if (ipAccountId is not null)
        {
            orders = orders.Where(o => o.IpAccountId == ipAccountId);
        }

        if (status is not null)
        {
            orders = orders.Where(o => o.Status == status);
        }

        var result = await orders.OrderByDescending(o => o.CreatedAt).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(PaymentGatewayService.ToDto));
    }

    private static async Task<IResult> RefreshLink(Guid id, PaymentGatewayService gateway, CancellationToken ct) => (await gateway.RefreshAsync(id, ct)).ToOk();

    /// <summary>
    /// Gateway webhook: body capped at 64 KB, signature verified by the gateway adapter (constant time), then the order's
    /// real status is fetched from the gateway. 401 for a bad signature; 200 for anything else we accept or ignore.
    /// </summary>
    /// <param name="facility">Optional <c>?facility=</c> for facilities with their own merchant account; the order in the payload decides first.</param>
    private static async Task<IResult> Webhook(HttpRequest request, Guid? facility, PaymentGatewayService gateway, CancellationToken ct)
    {
        if (await ReadBodyAsync(request, ct) is not { } payload)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var signature = SignatureHeaders.Select(h => request.Headers[h].ToString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // Razorpay names each event (the same id on every redelivery); other gateways are recognised by a hash of the body.
        var eventId = request.Headers[RazorpayEventIdHeader].ToString();
        var provider = string.IsNullOrEmpty(eventId) && string.IsNullOrEmpty(request.Headers[SignatureHeaders[0]].ToString()) ? "gateway" : "razorpay";
        return Answer(await gateway.HandleWebhookAsync(payload, signature, null, eventId, provider, facility, ct));
    }

    /// <summary>
    /// Cashfree's webhook: <c>x-webhook-signature</c> over <c>x-webhook-timestamp</c> and the raw body, checked with
    /// Cashfree's client secret in constant time, and refused when signed too long ago. Cashfree sends no event id, so a
    /// redelivery is recognised by a hash of its body.
    /// </summary>
    /// <param name="facility">Optional <c>?facility=</c> for a facility with its own Cashfree account; the payment in the body decides first.</param>
    private static async Task<IResult> CashfreeWebhook(HttpRequest request, Guid? facility, PaymentGatewayService gateway, CancellationToken ct)
    {
        if (await ReadBodyAsync(request, ct) is not { } payload)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var signature = request.Headers["x-webhook-signature"].ToString();
        var timestamp = request.Headers["x-webhook-timestamp"].ToString();
        return Answer(await gateway.HandleWebhookAsync(
            payload, signature, timestamp, null, PaymentGatewayService.CashfreeWebhooks, facility, ct));
    }

    private static IResult Answer(WebhookOutcome outcome) => outcome switch
    {
        WebhookOutcome.InvalidSignature => Results.Unauthorized(),
        WebhookOutcome.NotConfigured => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        _ => Results.Ok(new WebhookAck(outcome == WebhookOutcome.Processed)),
    };

    /// <summary>
    /// The body as text, or null when it is longer than <see cref="MaxWebhookChars"/>: one character past the limit is
    /// read (asynchronously: Kestrel forbids synchronous body reads) to detect an oversize body without a Content-Length;
    /// the request stream stays open for the server.
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > MaxWebhookChars)
        {
            return null;
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[MaxWebhookChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return read > MaxWebhookChars ? null : new string(buffer, 0, read);
    }
}
