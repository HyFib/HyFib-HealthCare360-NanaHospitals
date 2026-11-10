namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>Non-secret payment gateway configuration (<c>integrations.payment</c>), INT-PG.</summary>
internal sealed record PaymentSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary>
    /// The gateway new orders and links go through: <c>razorpay</c>, <c>cashfree</c> or <c>simulator</c>. Each gateway's
    /// account below stays set up after a switch, so what the other one took is still checked, refunded and reconciled.
    /// </summary>
    public string? Provider { get; init; }

    public string BaseUrl { get; init; } = "https://api.razorpay.com";

    /// <summary>Razorpay key id (rzp_live_… / rzp_test_…). Public by design: Checkout needs it in the browser.</summary>
    public string? KeyId { get; init; }

    /// <summary>Where the gateway sends the patient after paying a link (optional; Razorpay and Cashfree).</summary>
    public string? PaymentLinkCallbackUrl { get; init; }

    /// <summary>
    /// Let the gateway also SMS/e-mail the link (Razorpay and Cashfree). Off by default: HC360 sends links through its
    /// own templates.
    /// </summary>
    public bool NotifyByRazorpay { get; init; }

    /// <summary>Cashfree's API: <c>https://api.cashfree.com/pg</c>, or <c>https://sandbox.cashfree.com/pg</c> for its test mode.</summary>
    public string CashfreeBaseUrl { get; init; } = CashfreeGateway.ProductionBaseUrl;

    /// <summary>Cashfree's client id (App ID), sent as <c>x-client-id</c>; the client secret goes in the credentials.</summary>
    public string? CashfreeClientId { get; init; }

    public int PaymentLinkExpiryHours { get; init; } = 72;

    /// <summary>
    /// Simulator only: days until captured payments are settled (1 is next day at 10:00 IST, like a real gateway's
    /// T+1; 0 settles at once when settlements are fetched, for training and tests).
    /// </summary>
    public int SimulatorSettlementDays { get; init; } = 1;
}

/// <summary>Secret payment configuration (<c>integrations.payment.secret</c>).</summary>
internal sealed record PaymentSecrets
{
    /// <summary>API key secret: HTTP basic auth and the checkout / payment-link signature key.</summary>
    public string? KeySecret { get; init; }

    /// <summary>Webhook secret configured in the Razorpay dashboard (signs webhook bodies).</summary>
    public string? WebhookSecret { get; init; }

    /// <summary>Optional fixed key for simulator signatures (otherwise a random per-process key is used).</summary>
    public string? SimulatorSecret { get; init; }

    /// <summary>
    /// Cashfree's client secret: the API's <c>x-client-secret</c> and the key its webhooks are signed with. Kept in its own
    /// row (<see cref="CashfreeSecrets.Key"/>) and filled in from there when it is read.
    /// </summary>
    public string? CashfreeClientSecret { get; init; }
}

/// <summary>Cashfree's credentials, in their own secret row so that replacing Razorpay's never wipes them, nor the other way round.</summary>
internal sealed record CashfreeSecrets
{
    public const string Key = "integrations.payment.cashfree.secret";

    public string? ClientSecret { get; init; }
}

internal interface IPaymentProvider
{
    string Key { get; }

    /// <summary>The gateway's name for staff (Razorpay, Cashfree, the training gateway).</summary>
    string Name { get; }

    /// <summary>Whether these settings hold this gateway's account (its id and secret), whichever gateway new payments use.</summary>
    bool IsSetUp(PaymentSettings settings, PaymentSecrets secrets);

    Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<PaymentStatusResult> GetStatusAsync(string orderId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<RefundResult> RefundAsync(
        GatewayPaymentRef payment, decimal amount, string reason, string? idempotencyKey, PaymentSettings settings,
        PaymentSecrets secrets, CancellationToken cancellationToken);

    /// <summary>A checkout's signature (Razorpay: <c>order_id|payment_id</c> with the key secret).</summary>
    bool VerifySignature(string payload, string signature, PaymentSecrets secrets);

    /// <summary>A webhook's signature over its raw body (and, where the gateway signs one, its timestamp at <paramref name="now"/>).</summary>
    bool VerifyWebhook(string payload, string signature, string? timestamp, PaymentSecrets secrets, DateTimeOffset now);

    /// <summary>The settlements of one Indian calendar day; throws <see cref="IntegrationException"/> when they cannot be read in full.</summary>
    Task<IReadOnlyList<PaymentSettlement>> GetSettlementsAsync(DateOnly settledOn, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<RefundStatusResult> GetRefundStatusAsync(
        string refundId, GatewayPaymentRef payment, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    /// <summary>One dispute by its id; throws <see cref="IntegrationException"/> when it cannot be read.</summary>
    Task<GatewayDispute> GetDisputeAsync(string disputeId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    /// <summary>Every dispute raised since <paramref name="since"/>; throws <see cref="IntegrationException"/> when they cannot all be read.</summary>
    Task<IReadOnlyList<GatewayDispute>> GetDisputesAsync(DateTimeOffset since, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);
}

/// <summary>Rupee ↔ paise conversion with the validation every gateway call needs.</summary>
internal static class PaymentAmounts
{
    /// <summary>₹1 is the gateway minimum; the upper bound catches unit mistakes (paise passed as rupees) early.</summary>
    public static long ToPaise(decimal amount)
    {
        if (amount < 1m || amount > 10_000_000m)
        {
            throw new IntegrationException("Online payments must be between ₹1 and ₹1,00,00,000.");
        }

        return (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
    }

    public static decimal FromPaise(long paise) => paise / 100m;
}
