using System.Globalization;
using System.Text;
using System.Xml;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// Posts vouchers to Tally Prime's XML server in batches and reads a day's vouchers back. Every voucher is validated
/// first (balanced, positive amounts, ledgers present) so a single bad voucher is reported by number instead of failing a
/// whole envelope with Tally's generic "voucher totals do not match". Vouchers carry a REMOTEID, so a retried batch alters
/// rather than duplicates.
/// <para>
/// Tally answers a batch with counts (created, altered, errors), not per voucher. When a batch's counts do not account for
/// every voucher in it, its vouchers are sent again one at a time to learn which ones Tally refused and why; the REMOTEID
/// makes the second sending alter what the first one created.
/// </para>
/// <para>
/// The caller's token is its deadline: once it is cancelled no further envelope is sent, a call in progress is cut
/// short, and the vouchers not answered come back as not sent rather than as an exception, so the caller keeps every
/// answer Tally gave before the time ran out.
/// </para>
/// </summary>
internal sealed class TallyPrimeExporter(HttpClient http, IntegrationTrace trace) : IAccountingProvider
{
    /// <summary>
    /// Reading a day's vouchers is streamed, so only time bounds it. The check that reads it runs as an outbox message,
    /// which has 30 seconds in all.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    private const string OutOfTime = "Not sent: the attempt ran out of time. The next attempt sends it.";

    public string Key => ProviderKeys.TallyPrime;

    public async Task<bool> PingAsync(TallySettings settings, CancellationToken cancellationToken) =>
        await CheckCompanyAsync(settings, cancellationToken) is null;

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
            return new AccountingExportResult(false, 0, 0, invalid.Count, message) { Vouchers = Refused(vouchers) };
        }

        if (!IntegrationHttp.TryCreateEndpoint(settings.Url, requireHttps: false, out var uri))
        {
            return NotSent(vouchers, "The Tally URL is not a valid http(s) address.");
        }

        var options = TallyImportOptions.From(settings);
        var answers = new List<AccountingVoucherAnswer>(vouchers.Count);
        var messages = new List<string>();
        foreach (var batch in vouchers.Chunk(Math.Clamp(settings.BatchSize, 1, 200)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                messages.Add(OutOfTime);
                answers.AddRange(vouchers.Skip(answers.Count)
                    .Select(v => new AccountingVoucherAnswer(v.Key, AccountingVoucherOutcome.NotSent, OutOfTime)));
                break;
            }

            var sent = await SendAsync(uri, batch, options, cancellationToken);
            if (sent.Failure is { } failure)
            {
                // Tally is down or unreachable: later batches would fail the same way. Report and let the caller retry.
                messages.Add(failure);
                answers.AddRange(vouchers.Skip(answers.Count).Select(v => new AccountingVoucherAnswer(v.Key, AccountingVoucherOutcome.NotSent, failure)));
                break;
            }

            var result = sent.Result!;
            if (result.Success && result.Created + result.Altered == batch.Length)
            {
                var outcome = result.Created == batch.Length ? AccountingVoucherOutcome.Created
                    : result.Altered == batch.Length ? AccountingVoucherOutcome.Altered
                    : AccountingVoucherOutcome.Accepted;
                answers.AddRange(batch.Select(v => new AccountingVoucherAnswer(v.Key, outcome)));
                continue;
            }

            if (batch.Length == 1)
            {
                answers.Add(Answer(batch[0], result));
                continue;
            }

            // The counts do not say which vouchers Tally refused: ask again one voucher at a time, while time allows.
            foreach (var voucher in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    answers.Add(new AccountingVoucherAnswer(voucher.Key, AccountingVoucherOutcome.NotSent, OutOfTime));
                    continue;
                }

                var single = await SendAsync(uri, [voucher], options, cancellationToken);
                answers.Add(single.Failure is { } error
                    ? new AccountingVoucherAnswer(voucher.Key, AccountingVoucherOutcome.NotSent, error)
                    : Answer(voucher, single.Result!));
            }
        }

        messages.AddRange(answers.Where(a => a.Outcome is AccountingVoucherOutcome.Rejected or AccountingVoucherOutcome.Unconfirmed)
            .Select(a => $"voucher {a.Key}: {a.Message}"));
        var created = answers.Count(a => a.Outcome == AccountingVoucherOutcome.Created);
        var altered = answers.Count(a => a.Outcome is AccountingVoucherOutcome.Altered or AccountingVoucherOutcome.Accepted);
        var errors = answers.Count(a => a.Outcome is not (AccountingVoucherOutcome.Created or AccountingVoucherOutcome.Altered or AccountingVoucherOutcome.Accepted));
        var success = errors == 0;
        return new AccountingExportResult(success, created, altered, errors,
            success
                ? string.Create(CultureInfo.InvariantCulture, $"Tally created {created} and altered {altered} voucher(s).")
                : IntegrationTrace.Truncate(string.Join(" | ", messages.Distinct(StringComparer.Ordinal).Take(5)), 1000))
        {
            Vouchers = answers,
        };
    }

    public async Task<TallyDayRead> ReadDayAsync(DateOnly date, TallySettings settings, CancellationToken cancellationToken)
    {
        if (!IntegrationHttp.TryCreateEndpoint(settings.Url, requireHttps: false, out var uri))
        {
            return TallyDayRead.Failed("The Tally URL is not a valid http(s) address.");
        }

        // A closed company would answer with no vouchers, which would read as "everything is missing": check it first.
        if (await CheckCompanyAsync(settings, cancellationToken) is { } problem)
        {
            return TallyDayRead.Failed(problem);
        }

        var started = trace.Start();
        var reference = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(TallyXml.BuildDayVouchersRequest(date, settings.CompanyName), Encoding.UTF8, "text/xml"),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var read = await TallyVoucherReader.ReadAsync(body, timeout.Token);
            var ours = read.Vouchers.Count(v => v.RemoteId?.StartsWith(AccountingVoucher.RemoteIdPrefix, StringComparison.OrdinalIgnoreCase) == true);
            await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.read", read.Error is null, started, reference, "day vouchers",
                string.Create(CultureInfo.InvariantCulture, $"{read.Vouchers.Count} voucher(s), {ours} from HC360"), read.Error);
            return read.Error is { } error
                ? TallyDayRead.Failed("Tally could not export the day's vouchers: " + IntegrationTrace.Truncate(error, 300))
                : new TallyDayRead(true, null, read.Vouchers);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            var error = ex is InvalidDataException ? ex.Message : "Tally returned an answer HC360 could not read as XML.";
            await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.read", false, started, reference, error: error);
            return TallyDayRead.Failed(error);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "Tally");
            await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.read", false, started, reference, error: error);
            return TallyDayRead.Failed(error);
        }
    }

    /// <summary>
    /// Answers for a set of vouchers that is not sent because some would be refused: those are rejected with the reason,
    /// the others wait (a day book is sent whole, so the books never hold half a day by mistake).
    /// </summary>
    public static IReadOnlyList<AccountingVoucherAnswer> Refused(IReadOnlyList<AccountingVoucher> vouchers) =>
        vouchers.Select(v => TallyXml.Validate(v) is { } problem
                ? new AccountingVoucherAnswer(v.Key, AccountingVoucherOutcome.Rejected, $"HC360 did not send it: {problem}.")
                : new AccountingVoucherAnswer(v.Key, AccountingVoucherOutcome.NotSent, "Not sent: another voucher of the day cannot be exported."))
            .ToList();

    private static AccountingExportResult NotSent(IReadOnlyList<AccountingVoucher> vouchers, string message) =>
        new(false, 0, 0, vouchers.Count, message)
        {
            Vouchers = vouchers.Select(v => new AccountingVoucherAnswer(v.Key, AccountingVoucherOutcome.NotSent, message)).ToList(),
        };

    /// <summary>The answer for a voucher sent on its own.</summary>
    private static AccountingVoucherAnswer Answer(AccountingVoucher voucher, AccountingExportResult result) =>
        result.Errors > 0 || !result.Success
            ? new AccountingVoucherAnswer(voucher.Key, AccountingVoucherOutcome.Rejected, result.Message ?? "Tally refused the voucher.")
            : result.Created > 0 ? new AccountingVoucherAnswer(voucher.Key, AccountingVoucherOutcome.Created)
            : result.Altered > 0 ? new AccountingVoucherAnswer(voucher.Key, AccountingVoucherOutcome.Altered)
            : new AccountingVoucherAnswer(voucher.Key, AccountingVoucherOutcome.Unconfirmed, "Tally neither created nor altered the voucher.");

    /// <summary>
    /// Sends one import envelope; a transport failure, or the caller's deadline passing during the call, comes back as
    /// text, never as an exception.
    /// </summary>
    private async Task<(AccountingExportResult? Result, string? Failure)> SendAsync(
        Uri uri, IReadOnlyList<AccountingVoucher> batch, TallyImportOptions options, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var reference = batch.Count == 1 ? batch[0].Number : $"{batch[0].Number}..{batch[^1].Number}";
        try
        {
            var result = TallyResponseParser.Parse(await PostAsync(uri, TallyXml.BuildImport(batch, options), cancellationToken));
            await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.import", result.Success, started, reference,
                string.Create(CultureInfo.InvariantCulture, $"{batch.Count} voucher(s)"), result.Message, result.Success ? null : result.Message);
            return (result, null);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "Tally");
            await trace.LogAsync(IntegrationNames.Tally, Key, "vouchers.import", false, started, reference, error: error);
            return (null, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Tally may still apply the envelope; the REMOTEIDs make the next attempt alter rather than duplicate it.
            const string error = "Tally did not answer before the attempt ran out of time.";
            await trace.LogAsync(
                IntegrationNames.Tally, Key, "vouchers.import", false, started, reference, error: error);
            return (null, error);
        }
    }

    /// <summary>Null when Tally answers and the configured company is open; otherwise why not.</summary>
    private async Task<string?> CheckCompanyAsync(TallySettings settings, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (!IntegrationHttp.TryCreateEndpoint(settings.Url, requireHttps: false, out var uri))
        {
            const string invalid = "The Tally URL is not a valid http(s) address.";
            await trace.LogAsync(IntegrationNames.Tally, Key, "ping", false, started, error: invalid);
            return invalid;
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
            return error;
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "Tally");
            await trace.LogAsync(IntegrationNames.Tally, Key, "ping", false, started, error: error);
            return error;
        }
    }

    private async Task<string> PostAsync(Uri uri, string xml, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
