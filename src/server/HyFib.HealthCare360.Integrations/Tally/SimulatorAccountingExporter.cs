using System.Globalization;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Simulator;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// Training "Tally": validates vouchers exactly like the real exporter, keeps them in a simulated company
/// (<see cref="SimulatedTallyCompany"/>: a voucher imported again is altered, as in Tally) and stores the import XML it
/// would have sent in the simulator inbox, so accountants can practise posting and the read-back check without a Tally
/// installation.
/// </summary>
internal sealed class SimulatorAccountingExporter(SimulatorInbox inbox, SimulatedTallyCompany company, IntegrationTrace trace) : IAccountingProvider
{
    private const string DefaultCompany = "Tally (simulator)";

    public string Key => ProviderKeys.Simulator;

    public Task<bool> PingAsync(TallySettings settings, CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, TallySettings settings, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var invalid = vouchers.Select(v => (v.Number, Problem: TallyXml.Validate(v))).Where(v => v.Problem is not null).ToList();
        if (invalid.Count > 0)
        {
            return new AccountingExportResult(false, 0, 0, invalid.Count, string.Join("; ", invalid.Take(5).Select(v => $"voucher {v.Number}: {v.Problem}")))
            {
                Vouchers = TallyPrimeExporter.Refused(vouchers),
            };
        }

        var options = TallyImportOptions.From(settings);
        var outcomes = await company.ImportAsync(CompanyOf(settings), vouchers.Select(v => (v, TallyXml.Voucher(v, options))).ToList(), null, cancellationToken);
        var xml = TallyXml.BuildImport(vouchers, options);
        var reference = vouchers.Count == 0 ? null : vouchers.Count == 1 ? vouchers[0].Number : $"{vouchers[0].Number}..{vouchers[^1].Number}";
        var created = outcomes.Count(o => o == AccountingVoucherOutcome.Created);
        var altered = outcomes.Count - created;
        var summary = string.Create(CultureInfo.InvariantCulture, $"{vouchers.Count} voucher(s): {created} created, {altered} altered");
        var id = await inbox.RecordAsync(SimulatorChannel.Accounting, settings.CompanyName ?? DefaultCompany, summary, xml, null, reference, cancellationToken);
        await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.import", true, started, reference, summary, id);
        return new AccountingExportResult(true, created, altered, 0,
            string.Create(CultureInfo.InvariantCulture, $"Simulator created {created} and altered {altered} voucher(s)."))
        {
            Vouchers = vouchers.Select((v, i) => new AccountingVoucherAnswer(v.Key, outcomes[i])).ToList(),
        };
    }

    public async Task<TallyDayRead> ReadDayAsync(DateOnly date, TallySettings settings, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var read = TallyVoucherReader.Read(await company.ExportDayAsync(CompanyOf(settings), date, cancellationToken));
        await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.read", read.Error is null, started, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "day vouchers", string.Create(CultureInfo.InvariantCulture, $"{read.Vouchers.Count} voucher(s)"), read.Error);
        return read.Error is { } error ? TallyDayRead.Failed(error) : new TallyDayRead(true, null, read.Vouchers);
    }

    private static string CompanyOf(TallySettings settings) =>
        string.IsNullOrWhiteSpace(settings.CompanyName) ? DefaultCompany : settings.CompanyName.Trim();
}
