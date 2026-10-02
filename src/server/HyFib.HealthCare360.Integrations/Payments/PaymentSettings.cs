namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>Non-secret payment gateway configuration (<c>integrations.payment</c>), INT-PG.</summary>
internal sealed record PaymentSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>razorpay</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    public string BaseUrl { get; init; } = "https://api.razorpay.com";

    /// <summary>Razorpay key id (rzp_live_… / rzp_test_…). Public by design: Checkout needs it in the browser.</summary>
    public string? KeyId { get; init; }

    /// <summary>Where Razorpay redirects the patient after paying a link (optional).</summary>
    public string? PaymentLinkCallbackUrl { get; init; }

    /// <summary>Let Razorpay also SMS/e-mail the link. Off by default: HC360 sends links through its own templates.</summary>
    public bool NotifyByRazorpay { get; init; }

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
}

internal interface IPaymentProvider
{
    string Key { get; }

    Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<PaymentStatusResult> GetStatusAsync(string orderId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<RefundResult> RefundAsync(string paymentId, decimal amount, string reason, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    bool VerifySignature(string payload, string signature, PaymentSecrets secrets);

    /// <summary>The settlements of one Indian calendar day; throws <see cref="IntegrationException"/> when they cannot be read in full.</summary>
    Task<IReadOnlyList<PaymentSettlement>> GetSettlementsAsync(DateOnly settledOn, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);

    Task<RefundStatusResult> GetRefundStatusAsync(string refundId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken);
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
