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
        // Each call is logged against the facility whose account it uses: webhooks and re-checks have no signed-in user.
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (provider, config) = await ResolveAsync(facilityId, cancellationToken);
        return await provider.CreateOrderAsync(request, config.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (provider, config) = await ResolveAsync(facilityId, cancellationToken);
        return await provider.CreatePaymentLinkAsync(request, config.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<PaymentStatusResult> GetStatusAsync(string orderId, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
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

    public async Task<RefundResult> RefundAsync(
        string paymentId, decimal amount, string reason, string? idempotencyKey, Guid? facilityId,
        CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        try
        {
            var (provider, config) = await ResolveAsync(facilityId, cancellationToken);
            return await provider.RefundAsync(
                paymentId, amount, reason, idempotencyKey, config.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            return new RefundResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Settlements of one day for the facility's merchant account. Read even while online payments are switched off, so
    /// the money taken before that is still reconciled; a gateway that is not set up reports itself unavailable.
    /// </summary>
    public async Task<PaymentSettlementReport> GetSettlementsAsync(DateOnly settledOn, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (provider, config, problem) = await ResolveForReadingAsync(facilityId, cancellationToken);
        if (provider is null)
        {
            return PaymentSettlementReport.Unavailable(config.Settings?.Provider ?? "none", problem!);
        }

        try
        {
            var settlements = await provider.GetSettlementsAsync(settledOn, config.Settings!, config.Secrets, cancellationToken);
            return new PaymentSettlementReport(provider.Key, true, settlements, null);
        }
        catch (IntegrationException ex)
        {
            return PaymentSettlementReport.Unavailable(provider.Key, ex.Message);
        }
    }

    /// <summary>A refund's status at the gateway; when the gateway cannot be asked, Pending with the reason.</summary>
    public async Task<RefundStatusResult> GetRefundStatusAsync(string refundId, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (provider, config, problem) = await ResolveForReadingAsync(facilityId, cancellationToken);
        if (provider is null)
        {
            return new RefundStatusResult(refundId, PaymentRefundStatus.Pending, null, null, null, null, problem);
        }

        try
        {
            return await provider.GetRefundStatusAsync(refundId, config.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            return new RefundStatusResult(refundId, PaymentRefundStatus.Pending, null, null, null, null, ex.Message);
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

    /// <summary>The configured provider whether or not online payments are switched on (reconciliation of past payments).</summary>
    private async Task<(IPaymentProvider? Provider, IntegrationConfig<PaymentSettings, PaymentSecrets> Config, string? Problem)> ResolveForReadingAsync(
        Guid? facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<PaymentSettings, PaymentSecrets>(IntegrationNames.Payment, facilityId, requireEnabled: false, cancellationToken);
        if (config.Problem is { } problem)
        {
            return (null, config, problem);
        }

        var key = config.Settings?.Provider?.Trim().ToLowerInvariant();
        var provider = providers.FirstOrDefault(p => p.Key == key);
        return provider is null
            ? (null, config, string.IsNullOrEmpty(key) ? "No payment gateway is chosen. Set it up in Administration → Integrations." : $"Unknown payment gateway '{key}'. Use razorpay or simulator.")
            : (provider, config, null);
    }
}
