namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// The platform's <see cref="IAccountingExporter"/>: delegates pushes, pings and day read-backs to the facility's
/// configured provider and builds the manual-import file (Tally → Import → Vouchers) used when the accounts PC is
/// unreachable.
/// <para>
/// "Not configured" means there are no Tally settings, or posting is switched off: nothing is sent and the caller
/// should not retry (the day book is imported from a file instead). Settings that exist but cannot be used (an unknown
/// provider, values of the wrong type) are a failure, so they show as errors until someone corrects them.
/// </para>
/// </summary>
internal sealed class AccountingExporterRouter(
    IEnumerable<IAccountingProvider> providers,
    IntegrationSettingsReader settings,
    ICurrentUser currentUser) : IAccountingExporter
{
    public string Provider => "configured";

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        var (provider, config, _) = await ResolveAsync(null, cancellationToken);
        return provider is not null && await provider.PingAsync(config.Settings!, cancellationToken);
    }

    public async Task<AccountingConnection> GetConnectionAsync(Guid? facilityId, CancellationToken cancellationToken) =>
        (await ResolveAsync(facilityId, cancellationToken)).Connection;

    public async Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, Guid? facilityId, CancellationToken cancellationToken)
    {
        var (provider, config, connection) = await ResolveAsync(facilityId, cancellationToken);
        if (provider is not null)
        {
            return await provider.PushVouchersAsync(vouchers, config.Settings!, cancellationToken);
        }

        var notConfigured = connection.State == AccountingConnectionState.NotConfigured;
        return new AccountingExportResult(false, 0, 0, notConfigured ? 0 : vouchers.Count, connection.Message)
        {
            NotConfigured = notConfigured,
            Vouchers = vouchers.Select(v => new AccountingVoucherAnswer(v.Key, AccountingVoucherOutcome.NotSent, connection.Message)).ToList(),
        };
    }

    public async Task<AccountingReadBack> ReadBackAsync(DateOnly date, IReadOnlyList<AccountingVoucher> expected, Guid? facilityId, CancellationToken cancellationToken)
    {
        var (provider, config, connection) = await ResolveAsync(facilityId, cancellationToken);
        if (provider is null)
        {
            return new AccountingReadBack(false, connection.Message, [], []) { NotConfigured = connection.State == AccountingConnectionState.NotConfigured };
        }

        var day = await provider.ReadDayAsync(date, config.Settings!, cancellationToken);
        return day.Success
            ? TallyReadBack.Compare(date, expected, day.Vouchers, TallyImportOptions.From(config.Settings!))
            : new AccountingReadBack(false, day.Error, [], []);
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

    private async Task<(IAccountingProvider? Provider, IntegrationConfig<TallySettings, NoSecrets> Config, AccountingConnection Connection)> ResolveAsync(
        Guid? facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<TallySettings, NoSecrets>(IntegrationNames.Tally, facilityId, requireEnabled: true, cancellationToken);
        var provider = config.ActiveProvider is { } key ? providers.FirstOrDefault(p => p.Key == key) : null;
        if (provider is not null)
        {
            return (provider, config, new AccountingConnection(AccountingConnectionState.Ready, provider.Key, null));
        }

        if (config.Settings is { Enabled: false })
        {
            return (null, config, new AccountingConnection(AccountingConnectionState.NotConfigured, null,
                "Posting to Tally is switched off in Administration → Integrations."));
        }

        if (config.Settings is null && !await HasSettingsAsync(facilityId, cancellationToken))
        {
            return (null, config, new AccountingConnection(AccountingConnectionState.NotConfigured, null,
                "Tally is not set up. Set it up in Administration → Integrations, or import the day book file into Tally."));
        }

        var problem = config.Problem
            ?? (config.Settings?.Provider is { Length: > 0 } unknown
                ? $"'{unknown}' is not a Tally provider. Choose Tally Prime or the simulator in Administration → Integrations."
                : "Choose the Tally provider in Administration → Integrations.");
        return (null, config, new AccountingConnection(AccountingConnectionState.Invalid, config.Settings?.Provider, problem));
    }

    /// <summary>
    /// Whether any Tally settings row applies to the facility (the reader cannot tell a missing row from one it could not
    /// read). Stored values are JSON, so any row reads as an element.
    /// </summary>
    private async Task<bool> HasSettingsAsync(Guid? facilityId, CancellationToken cancellationToken) =>
        await settings.ReadValueAsync<JsonElement?>(IntegrationNames.SettingsKey(IntegrationNames.Tally), facilityId ?? currentUser.FacilityId, cancellationToken)
            is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
}
