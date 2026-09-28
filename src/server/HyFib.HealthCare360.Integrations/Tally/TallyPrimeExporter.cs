using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// Posts vouchers to Tally Prime's XML server in batches. Every voucher is validated first (balanced, positive
/// amounts, ledgers present) so a single bad voucher is reported by number instead of failing a whole envelope with
/// Tally's generic "voucher totals do not match". Vouchers carry a REMOTEID, so a retried batch alters rather than
/// duplicates.
/// </summary>
internal sealed class TallyPrimeExporter(HttpClient http, IntegrationTrace trace) : IAccountingProvider
{
    public string Key => ProviderKeys.TallyPrime;

    public async Task<bool> PingAsync(TallySettings settings, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (!IntegrationHttp.TryCreateEndpoint(settings.Url, requireHttps: false, out var uri))
        {
            await trace.LogAsync(IntegrationNames.Tally, Key, "ping", false, started, error: "The Tally URL is not a valid http(s) address.");
            return false;
        }

        try
        {
            var xml = await PostAsync(uri, TallyXml.BuildCompanyListRequest(), cancellationToken);
            var companies = TallyResponseParser.TryLoad(xml, out var document) ? TallyXml.ParseCompanyNames(document) : [];
            var companyOpen = string.IsNullOrWhiteSpace(settings.CompanyName)
                ? companies.Count > 0
                : companies.Contains(settings.CompanyName.Trim(), StringComparer.OrdinalIgnoreCase);
            var error = companyOpen ? null
                : string.IsNullOrWhiteSpace(settings.CompanyName) ? "No company is open in Tally."
                : $"Company '{settings.CompanyName}' is not open in Tally.";
            await trace.LogAsync(IntegrationNames.Tally, Key, "ping", companyOpen, started, null, "company list",
                string.Create(CultureInfo.InvariantCulture, $"{companies.Count} compan{(companies.Count == 1 ? "y" : "ies")} open"), error);
            return companyOpen;
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            await trace.LogAsync(IntegrationNames.Tally, Key, "ping", false, started, error: IntegrationErrors.Describe(ex, "Tally"));
            return false;
        }
    }

    public async Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, TallySettings settings, CancellationToken cancellationToken)
    {
        if (vouchers.Count == 0)
        {
            return new AccountingExportResult(true, 0, 0, 0, "Nothing to export.");
        }

        var invalid = vouchers.Select(v => (v.Number, Problem: TallyXml.Validate(v))).Where(v => v.Problem is not null).ToList();
        if (invalid.Count > 0)
        {
            var message = string.Join("; ", invalid.Take(5).Select(v => $"voucher {v.Number}: {v.Problem}"));
            return new AccountingExportResult(false, 0, 0, invalid.Count, message);
        }

        if (!IntegrationHttp.TryCreateEndpoint(settings.Url, requireHttps: false, out var uri))
        {
            return new AccountingExportResult(false, 0, 0, vouchers.Count, "The Tally URL is not a valid http(s) address.");
        }

        var options = TallyImportOptions.From(settings);
        int created = 0, altered = 0, errors = 0, answered = 0;
        var messages = new List<string>();
        foreach (var batch in vouchers.Chunk(Math.Clamp(settings.BatchSize, 1, 200)))
        {
            var started = trace.Start();
            var reference = batch.Length == 1 ? batch[0].Number : $"{batch[0].Number}..{batch[^1].Number}";
            try
            {
                var response = await PostAsync(uri, TallyXml.BuildImport(batch, options), cancellationToken);
                var result = TallyResponseParser.Parse(response);
                answered += batch.Length;
                created += result.Created;
                altered += result.Altered;
                errors += result.Errors;
                if (!result.Success && result.Message is { } error)
                {
                    messages.Add(error);
                }

                await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.import", result.Success, started, reference,
                    string.Create(CultureInfo.InvariantCulture, $"{batch.Length} voucher(s)"), result.Message, result.Success ? null : result.Message);
            }
            catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
            {
                // Tally is down or unreachable: later batches would fail the same way; report and let the caller retry.
                var error = IntegrationErrors.Describe(ex, "Tally");
                await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.import", false, started, reference, error: error);
                errors += vouchers.Count - answered;
                messages.Add(error);
                break;
            }
        }

        var success = errors == 0;
        return new AccountingExportResult(success, created, altered, errors,
            success ? string.Create(CultureInfo.InvariantCulture, $"Tally created {created} and altered {altered} voucher(s).") : string.Join(" | ", messages.Take(5)));
    }

    private async Task<string> PostAsync(Uri uri, string xml, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
