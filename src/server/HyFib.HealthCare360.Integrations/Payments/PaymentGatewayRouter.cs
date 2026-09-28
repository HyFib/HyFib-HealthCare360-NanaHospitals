namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// The platform's <see cref="IPaymentGateway"/>: delegates to the gateway configured for the facility each call names
/// (falling back to the signed-in facility, then the all-facility settings). Order and link creation
/// throw <see cref="IntegrationException"/> (an <see cref="InvalidOperationException"/>) when the gateway is not
/// usable, because the contract returns the order itself; status and refunds report failures in their results.
/// </summary>
internal sealed class PaymentGatewayRouter(IEnumerable<IPaymentProvider> providers, IntegrationSettingsReader settings) : IPaymentGateway
{
    public string Provider => "configured";

    public async Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, Guid? facilityId, CancellationToken cancellationToken)
    {
        var (provider, config) = await ResolveAsync(facilityId, cancellationToken);
        return await provider.CreateOrderAsync(request, config.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, Guid? facilityId, CancellationToken cancellationToken)
    {
        var (provider, config) = await ResolveAsync(facilityId, cancellationToken);
        return await provider.CreatePaymentLinkAsync(request, config.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<PaymentStatusResult> GetStatusAsync(string orderId, Guid? facilityId, CancellationToken cancellationToken)
    {
        try
        {
            var (provider, config) = await ResolveAsync(facilityId, cancellationToken);
            return await provider.GetStatusAsync(orderId, config.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Pending, null, null, null, ex.Message);
        }
    }

    public async Task<RefundResult> RefundAsync(string paymentId, decimal amount, string reason, Guid? facilityId, CancellationToken cancellationToken)
    {
        try
        {
            var (provider, config) = await ResolveAsync(facilityId, cancellationToken);
            return await provider.RefundAsync(paymentId, amount, reason, config.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            return new RefundResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Checkout (<c>order_id|payment_id</c>), payment-link and webhook signatures, checked with the secrets of the named
    /// facility's account (webhooks are anonymous, so the caller passes the facility of the order concerned).
    /// Configuration is read even when the gateway was disabled after payments were started, so late webhooks verify.
    /// </summary>
    public bool VerifySignature(string payload, string signature, Guid? facilityId)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var config = settings.Read<PaymentSettings, PaymentSecrets>(IntegrationNames.Payment, facilityId, requireEnabled: false);
        var key = config.Settings?.Provider?.Trim().ToLowerInvariant();
        var provider = providers.FirstOrDefault(p => p.Key == key);
        return provider is not null && config.Problem is null && provider.VerifySignature(payload, signature, config.Secrets);
    }

    private async Task<(IPaymentProvider Provider, IntegrationConfig<PaymentSettings, PaymentSecrets> Config)> ResolveAsync(
        Guid? facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<PaymentSettings, PaymentSecrets>(IntegrationNames.Payment, facilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            throw new IntegrationException(config.Problem ?? "The payment gateway is not available.");
        }

        var provider = providers.FirstOrDefault(p => p.Key == key)
            ?? throw new IntegrationException($"Unknown payment gateway '{key}'. Use razorpay or simulator.");
        return (provider, config);
    }
}
