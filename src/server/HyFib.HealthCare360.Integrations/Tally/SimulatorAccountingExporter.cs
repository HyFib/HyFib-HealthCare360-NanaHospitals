using System.Globalization;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Simulator;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// Training "Tally": validates vouchers exactly like the real exporter and stores the import XML it would have sent
/// in the simulator inbox, so accountants can review postings without a Tally installation.
/// </summary>
internal sealed class SimulatorAccountingExporter(SimulatorInbox inbox, IntegrationTrace trace) : IAccountingProvider
{
    public string Key => ProviderKeys.Simulator;

    public Task<bool> PingAsync(TallySettings settings, CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, TallySettings settings, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var invalid = vouchers.Select(v => (v.Number, Problem: TallyXml.Validate(v))).Where(v => v.Problem is not null).ToList();
        if (invalid.Count > 0)
        {
            return new AccountingExportResult(false, 0, 0, invalid.Count, string.Join("; ", invalid.Take(5).Select(v => $"voucher {v.Number}: {v.Problem}")));
        }

        var xml = TallyXml.BuildImport(vouchers, TallyImportOptions.From(settings));
        var reference = vouchers.Count == 0 ? null : vouchers.Count == 1 ? vouchers[0].Number : $"{vouchers[0].Number}..{vouchers[^1].Number}";
        var id = await inbox.RecordAsync(SimulatorChannel.Accounting, settings.CompanyName ?? "Tally (simulator)",
            string.Create(CultureInfo.InvariantCulture, $"{vouchers.Count} voucher(s)"), xml, null, reference, cancellationToken);
        await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.import", true, started, reference,
            string.Create(CultureInfo.InvariantCulture, $"{vouchers.Count} voucher(s)"), id);
        return new AccountingExportResult(true, vouchers.Count, 0, 0, string.Create(CultureInfo.InvariantCulture, $"Simulator recorded {vouchers.Count} voucher(s)."));
    }
}
