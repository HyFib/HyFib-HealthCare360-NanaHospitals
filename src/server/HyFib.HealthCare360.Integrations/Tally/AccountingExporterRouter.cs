namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// The platform's <see cref="IAccountingExporter"/>: delegates pushes and pings to the configured provider and builds
/// the manual-import file (Tally → Import → Vouchers) used when the accounts PC is unreachable.
/// </summary>
internal sealed class AccountingExporterRouter(IEnumerable<IAccountingProvider> providers, IntegrationSettingsReader settings) : IAccountingExporter
{
    public string Provider => "configured";

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        var (provider, config) = await ResolveAsync(cancellationToken);
        return provider is not null && await provider.PingAsync(config.Settings!, cancellationToken);
    }

    public async Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, CancellationToken cancellationToken)
    {
        var (provider, config) = await ResolveAsync(cancellationToken);
        return provider is null
            ? new AccountingExportResult(false, 0, 0, vouchers.Count, config.Problem ?? "Tally is not available.")
            : await provider.PushVouchersAsync(vouchers, config.Settings!, cancellationToken);
    }

    /// <summary>
    /// Same envelope as the live import, so the file can be loaded manually. Throws <see cref="IntegrationException"/>
    /// when a voucher would be rejected. Uses the (cached) settings synchronously because the contract is synchronous;
    /// it works even while the integration is disabled — that is exactly when a file is needed.
    /// </summary>
    public string BuildImportFile(IReadOnlyList<AccountingVoucher> vouchers)
    {
        var invalid = vouchers.Select(v => (v.Number, Problem: TallyXml.Validate(v))).FirstOrDefault(v => v.Problem is not null);
        if (invalid.Problem is not null)
        {
            throw new IntegrationException($"Voucher {invalid.Number} cannot be exported: {invalid.Problem}.");
        }

        var config = settings.Read<TallySettings, NoSecrets>(IntegrationNames.Tally, null, requireEnabled: false);
        var options = config.Settings is null ? new TallyImportOptions(null, "Primary Cost Category", null) : TallyImportOptions.From(config.Settings);
        return TallyXml.BuildImport(vouchers, options);
    }

    private async Task<(IAccountingProvider? Provider, IntegrationConfig<TallySettings, NoSecrets> Config)> ResolveAsync(CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<TallySettings, NoSecrets>(IntegrationNames.Tally, cancellationToken);
        var provider = config.ActiveProvider is { } key ? providers.FirstOrDefault(p => p.Key == key) : null;
        return (provider, config);
    }
}
