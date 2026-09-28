namespace HyFib.HealthCare360.Integrations.Pos;

/// <summary>
/// The platform's <see cref="IPosTerminal"/>: delegates to the facility's POS provider. It is always registered but
/// inactive until configured: configuration problems come back as a declined result with the reason, so the billing
/// counter can fall back to manual card entry.
/// <para>
/// Callers store sales keyed by (<see cref="Provider"/>, transaction id), so <see cref="Provider"/> reports the
/// provider that handled the last call in this scope (<c>none</c> when nothing is configured), and a sale that never
/// reached a terminal gets a unique <c>declined-…</c> id instead of an empty one.
/// </para>
/// </summary>
internal sealed class PosTerminalRouter(IEnumerable<IPosProvider> providers, IntegrationSettingsReader settings) : IPosTerminal
{
    private string? _provider;

    public string Provider => _provider ?? "configured";

    public async Task<PosTransactionResult> StartSaleAsync(PosSaleRequest request, CancellationToken cancellationToken)
    {
        var (provider, config, error) = await ResolveAsync(cancellationToken);
        var result = provider is null
            ? Declined(string.Empty, error)
            : await provider.StartSaleAsync(request, config!.Settings!, config.Secrets, cancellationToken);
        return string.IsNullOrEmpty(result.TransactionId)
            ? result with { TransactionId = "declined-" + Guid.CreateVersion7().ToString("N") }
            : result;
    }

    public async Task<PosTransactionResult> GetStatusAsync(string transactionId, CancellationToken cancellationToken)
    {
        var (provider, config, error) = await ResolveAsync(cancellationToken);
        return provider is null
            ? Declined(transactionId, error)
            : await provider.GetStatusAsync(transactionId, config!.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<PosTransactionResult> CancelAsync(string transactionId, CancellationToken cancellationToken)
    {
        var (provider, config, error) = await ResolveAsync(cancellationToken);
        return provider is null
            ? Declined(transactionId, error)
            : await provider.CancelAsync(transactionId, config!.Settings!, config.Secrets, cancellationToken);
    }

    internal static PosTransactionResult Declined(string transactionId, string? message) =>
        new(transactionId, PosTransactionStatus.Declined, null, null, null, message);

    private async Task<(IPosProvider? Provider, IntegrationConfig<PosSettings, PosSecrets>? Config, string? Error)> ResolveAsync(CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<PosSettings, PosSecrets>(IntegrationNames.Pos, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            _provider = "none";
            return (null, null, config.Problem ?? "The POS integration is not available.");
        }

        var provider = providers.FirstOrDefault(p => p.Key == key);
        _provider = provider?.Key ?? "none";
        return provider is null ? (null, null, $"Unknown POS provider '{key}'. Use pinelabs or simulator.") : (provider, config, null);
    }
}
