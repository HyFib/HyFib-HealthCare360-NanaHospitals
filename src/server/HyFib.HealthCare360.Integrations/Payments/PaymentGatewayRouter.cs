namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// The platform's <see cref="IPaymentGateway"/>. New orders and links go to the gateway configured for the facility each
/// call names (falling back to the signed-in facility, then the all-facility settings). A call about something a gateway
/// already holds names that gateway, and is answered with that gateway's account from the same settings, so a facility
/// that switched from one gateway to another still checks, refunds, reconciles and follows what the first one took.
/// Order and link creation throw <see cref="IntegrationException"/> (an <see cref="InvalidOperationException"/>) when the
/// gateway is not usable, because the contract returns the order itself; status and refunds report failures in their results.
/// </summary>
internal sealed class PaymentGatewayRouter(IEnumerable<IPaymentProvider> providers, IntegrationSettingsReader settings, TimeProvider time) : IPaymentGateway
{
    public string Provider => "configured";

    public async Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, Guid? facilityId, CancellationToken cancellationToken)
    {
        // Each call is logged against the facility whose account it uses: webhooks and re-checks have no signed-in user.
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (provider, config) = await ResolveAsync(facilityId, null, requireEnabled: true, cancellationToken);
        return await provider.CreateOrderAsync(request, config.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (provider, config) = await ResolveAsync(facilityId, null, requireEnabled: true, cancellationToken);
        return await provider.CreatePaymentLinkAsync(request, config.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<PaymentStatusResult> GetStatusAsync(string orderId, string? provider, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        try
        {
            var (gateway, config) = await ResolveAsync(facilityId, provider, requireEnabled: true, cancellationToken);
            return await gateway.GetStatusAsync(orderId, config.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Pending, null, null, null, ex.Message);
        }
    }

    public async Task<RefundResult> RefundAsync(
        GatewayPaymentRef payment, decimal amount, string reason, string? idempotencyKey, Guid? facilityId,
        CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        try
        {
            var (gateway, config) = await ResolveAsync(facilityId, payment.Provider, requireEnabled: true, cancellationToken);
            return await gateway.RefundAsync(
                payment, amount, reason, idempotencyKey, config.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            return new RefundResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// The gateway new payments go through (whether or not online payments are switched on), then every other gateway
    /// whose account is still set up: each one's settlements and disputes are read, so what a gateway took before a
    /// switch is still reconciled. None when the settings cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProvidersAsync(Guid? facilityId, CancellationToken cancellationToken)
    {
        var config = await ConfigAsync(facilityId, requireEnabled: false, cancellationToken);
        return Candidates(config).Select(p => p.Key).ToList();
    }

    /// <summary>The facility's gateway (as configured), then every other gateway whose account is still set up; none when the settings cannot be read.</summary>
    private IEnumerable<IPaymentProvider> Candidates(IntegrationConfig<PaymentSettings, PaymentSecrets> config)
    {
        if (config.Problem is not null || config.Settings is null)
        {
            yield break;
        }

        var current = Key(config.Settings.Provider);
        if (providers.FirstOrDefault(p => p.Key == current) is { } own)
        {
            yield return own;
        }

        foreach (var other in providers.Where(p => p.Key != current && p.IsSetUp(config.Settings, config.Secrets)))
        {
            yield return other;
        }
    }

    /// <summary>
    /// Settlements of one day for one gateway's account. Read even while online payments are switched off, so the money
    /// taken before that is still reconciled; a gateway that is not set up reports itself unavailable.
    /// </summary>
    public async Task<PaymentSettlementReport> GetSettlementsAsync(DateOnly settledOn, string? provider, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (gateway, config, problem) = await ResolveForReadingAsync(facilityId, provider, cancellationToken);
        if (gateway is null)
        {
            return PaymentSettlementReport.Unavailable(provider ?? config.Settings?.Provider ?? "none", problem!);
        }

        try
        {
            var settlements = await gateway.GetSettlementsAsync(settledOn, config.Settings!, config.Secrets, cancellationToken);
            return new PaymentSettlementReport(gateway.Key, true, settlements, null);
        }
        catch (IntegrationException ex)
        {
            return PaymentSettlementReport.Unavailable(gateway.Key, ex.Message);
        }
    }

    /// <summary>A refund's status at the gateway that made it; when the gateway cannot be asked, Pending with the reason.</summary>
    public async Task<RefundStatusResult> GetRefundStatusAsync(
        string refundId, GatewayPaymentRef payment, Guid? facilityId, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (gateway, config, problem) = await ResolveForReadingAsync(facilityId, payment.Provider, cancellationToken);
        if (gateway is null)
        {
            return new RefundStatusResult(refundId, PaymentRefundStatus.Pending, null, null, null, null, problem);
        }

        try
        {
            return await gateway.GetRefundStatusAsync(refundId, payment, config.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            return new RefundStatusResult(refundId, PaymentRefundStatus.Pending, null, null, null, null, ex.Message);
        }
    }

    /// <summary>A dispute as the named gateway holds it; read even while online payments are switched off.</summary>
    public Task<GatewayDisputeReport> GetDisputeAsync(string disputeId, string? provider, Guid? facilityId, CancellationToken cancellationToken) =>
        ReadDisputesAsync(facilityId, provider, async (gateway, config) => [await gateway.GetDisputeAsync(disputeId, config.Settings!, config.Secrets, cancellationToken)], cancellationToken);

    /// <summary>The disputes raised at the named gateway since <paramref name="since"/>, all or none.</summary>
    public Task<GatewayDisputeReport> GetDisputesAsync(DateTimeOffset since, string? provider, Guid? facilityId, CancellationToken cancellationToken) =>
        ReadDisputesAsync(facilityId, provider, (gateway, config) => gateway.GetDisputesAsync(since, config.Settings!, config.Secrets, cancellationToken), cancellationToken);

    /// <summary>
    /// A checkout's signature (<c>order_id|payment_id</c>), checked with the named gateway's account at the named
    /// facility. Configuration is read even when the gateway was disabled after payments were started.
    /// </summary>
    public bool VerifySignature(string payload, string signature, string? provider, Guid? facilityId)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var (gateway, config) = ResolveForVerifying(facilityId, provider);
        return gateway is not null && gateway.VerifySignature(payload, signature, config.Secrets);
    }

    /// <summary>
    /// The gateway whose account at the named facility signed a webhook (webhooks are anonymous, so the caller passes the
    /// facility of the order concerned): the named gateway only, or for the shared route the facility's gateway, then each
    /// other gateway still set up. Read even when the gateway was disabled after payments were started, and when the
    /// facility has switched to another gateway since, so late webhooks still verify.
    /// </summary>
    public string? VerifyWebhook(string? provider, string payload, string signature, string? timestamp, Guid? facilityId)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (provider is not null)
        {
            var (gateway, config) = ResolveForVerifying(facilityId, provider);
            return gateway is not null && gateway.VerifyWebhook(payload, signature, timestamp, config.Secrets, now) ? gateway.Key : null;
        }

        var facility = ConfigAsync(facilityId, requireEnabled: false, CancellationToken.None).GetAwaiter().GetResult();
        return Candidates(facility).FirstOrDefault(g => g.VerifyWebhook(payload, signature, timestamp, facility.Secrets, now))?.Key;
    }

    /// <summary>The facility's payment settings and credentials, Cashfree's client secret read from its own row.</summary>
    private async Task<IntegrationConfig<PaymentSettings, PaymentSecrets>> ConfigAsync(Guid? facilityId, bool requireEnabled, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<PaymentSettings, PaymentSecrets>(IntegrationNames.Payment, facilityId, requireEnabled, cancellationToken);
        if (config.Problem is not null)
        {
            return config;
        }

        var cashfree = await settings.ReadSecretAsync<CashfreeSecrets>(CashfreeSecrets.Key, facilityId, cancellationToken);
        return string.IsNullOrWhiteSpace(cashfree?.ClientSecret)
            ? config
            : config with { Secrets = config.Secrets with { CashfreeClientSecret = cashfree.ClientSecret } };
    }

    private static string? Key(string? provider) => string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();

    /// <summary>
    /// The named gateway (null: the facility's own) with the facility's settings. The facility's gateway is used as it
    /// is configured; another one only while its account is still set up there, with a message saying so otherwise.
    /// </summary>
    private async Task<(IPaymentProvider Provider, IntegrationConfig<PaymentSettings, PaymentSecrets> Config)> ResolveAsync(
        Guid? facilityId, string? provider, bool requireEnabled, CancellationToken cancellationToken)
    {
        var config = await ConfigAsync(facilityId, requireEnabled, cancellationToken);
        if (config.Problem is { } problem)
        {
            throw new IntegrationException(problem);
        }

        var current = Key(config.Settings?.Provider);
        var key = Key(provider) ?? current
            ?? throw new IntegrationException("No payment gateway is chosen. Set it up in Administration → Integrations.");
        var gateway = providers.FirstOrDefault(p => p.Key == key)
            ?? throw new IntegrationException($"Unknown payment gateway '{key}'. Use razorpay, cashfree or simulator.");
        if (key != current && !gateway.IsSetUp(config.Settings!, config.Secrets))
        {
            throw new IntegrationException(
                $"This was taken through {gateway.Name}, whose account is no longer set up for this facility. Set it up again in Administration → Integrations.");
        }

        return (gateway, config);
    }

    private async Task<GatewayDisputeReport> ReadDisputesAsync(
        Guid? facilityId, string? provider,
        Func<IPaymentProvider, IntegrationConfig<PaymentSettings, PaymentSecrets>, Task<IReadOnlyList<GatewayDispute>>> read,
        CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (gateway, config, problem) = await ResolveForReadingAsync(facilityId, provider, cancellationToken);
        if (gateway is null)
        {
            return GatewayDisputeReport.Unavailable(provider ?? config.Settings?.Provider ?? "none", problem!);
        }

        try
        {
            return new GatewayDisputeReport(gateway.Key, true, await read(gateway, config), null);
        }
        catch (IntegrationException ex)
        {
            return GatewayDisputeReport.Unavailable(gateway.Key, ex.Message);
        }
    }

    /// <summary>The named gateway whether or not online payments are switched on (reconciliation of past payments).</summary>
    private async Task<(IPaymentProvider? Provider, IntegrationConfig<PaymentSettings, PaymentSecrets> Config, string? Problem)> ResolveForReadingAsync(
        Guid? facilityId, string? provider, CancellationToken cancellationToken)
    {
        var config = await ConfigAsync(facilityId, requireEnabled: false, cancellationToken);
        return Choose(config, provider);
    }

    private (IPaymentProvider? Provider, IntegrationConfig<PaymentSettings, PaymentSecrets> Config) ResolveForVerifying(Guid? facilityId, string? provider)
    {
        var config = ConfigAsync(facilityId, requireEnabled: false, CancellationToken.None).GetAwaiter().GetResult();
        var (gateway, _, _) = Choose(config, provider);
        return (gateway, config);
    }

    private (IPaymentProvider? Provider, IntegrationConfig<PaymentSettings, PaymentSecrets> Config, string? Problem) Choose(
        IntegrationConfig<PaymentSettings, PaymentSecrets> config, string? provider)
    {
        if (config.Problem is { } problem)
        {
            return (null, config, problem);
        }

        var current = Key(config.Settings?.Provider);
        var key = Key(provider) ?? current;
        var gateway = providers.FirstOrDefault(p => p.Key == key);
        if (gateway is null)
        {
            return (null, config, string.IsNullOrEmpty(key)
                ? "No payment gateway is chosen. Set it up in Administration → Integrations."
                : $"Unknown payment gateway '{key}'. Use razorpay, cashfree or simulator.");
        }

        return key == current || gateway.IsSetUp(config.Settings!, config.Secrets)
            ? (gateway, config, null)
            : (null, config, $"{Capitalized(gateway.Name)}'s account is no longer set up for this facility. Set it up again in Administration → Integrations.");
    }

    private static string Capitalized(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
