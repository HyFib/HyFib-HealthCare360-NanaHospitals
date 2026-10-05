using HyFib.HealthCare360.Integrations.Laboratory.Orders;

namespace HyFib.HealthCare360.Integrations.Laboratory;

internal sealed record LabSubmitOutcome(bool Accepted, string? Error)
{
    public static LabSubmitOutcome Ok { get; } = new(true, null);
}

internal enum HostQueryStatus
{
    /// <summary>The LIS answered (possibly with no orders).</summary>
    Answered,

    /// <summary>Host queries are off for this analyser: acknowledge the query and answer nothing.</summary>
    TurnedOff,

    /// <summary>The LIS could not answer: refuse the query so the analyser asks again.</summary>
    Unavailable,
}

/// <summary>The LIS's answer to a host query.</summary>
internal sealed record HostQueryAnswer(HostQueryStatus Status, IReadOnlyList<InstrumentOrder> Orders, string? Error)
{
    public static HostQueryAnswer Answer(IReadOnlyList<InstrumentOrder> orders) => new(HostQueryStatus.Answered, orders, null);

    public static HostQueryAnswer Off(string reason) => new(HostQueryStatus.TurnedOff, [], reason);

    public static HostQueryAnswer Unavailable(string error) => new(HostQueryStatus.Unavailable, [], error);
}

/// <summary>What the protocol sessions need from the rest of HC360: the LIS (through the platform contract), the order queue and logging.</summary>
internal interface ILabMessageProcessor
{
    /// <summary>Hands decoded results to the LIS. Not accepted when no LIS is registered or it rejects a sample.</summary>
    Task<LabSubmitOutcome> SubmitResultsAsync(LabInstrumentSettings instrument, IReadOnlyList<InstrumentResultMessage> results, string rawMessage, CancellationToken cancellationToken);

    /// <summary>Orders for queried samples (host query); off unless the analyser is bidirectional in Lab setup.</summary>
    Task<HostQueryAnswer> QueryOrdersAsync(LabInstrumentSettings instrument, IReadOnlyList<string> sampleIds, string rawMessage, CancellationToken cancellationToken);

    /// <summary>The analyser's next orders to send unasked, built now (empty when there are none).</summary>
    Task<IReadOnlyList<LabDownload>> NextDownloadsAsync(LabInstrumentSettings instrument, int max, CancellationToken cancellationToken);

    /// <summary>Extends the session's claim on orders it is still sending, so no other session takes them.</summary>
    Task ExtendDownloadClaimAsync(
        LabInstrumentSettings instrument, IReadOnlyList<LabDownload> downloads, CancellationToken cancellationToken);

    /// <summary>Records whether the analyser accepted the orders (they are tried again later when it did not).</summary>
    Task CompleteDownloadsAsync(LabInstrumentSettings instrument, IReadOnlyList<LabDownload> downloads, bool delivered, string rawMessage, string? error, CancellationToken cancellationToken);

    /// <summary>Raw-message log entry (PHI masked) for messages that are not results or queries.</summary>
    Task LogAsync(LabInstrumentSettings instrument, string operation, bool success, string rawMessage, string? response, string? error, string? reference);
}

/// <summary>
/// Bridges analyser sessions (long-lived, outside any request) to scoped services: every message gets its own DI
/// scope, resolves <see cref="IInstrumentResultSink"/> (implemented by Diagnostics) and writes the masked raw message to
/// the integration log. When no sink is registered the message is logged and refused, so the analyser keeps it and
/// retransmits later instead of the result being silently dropped.
/// </summary>
internal sealed class LabMessageProcessor(IServiceScopeFactory scopes, LabGatewayState state, ILogger<LabMessageProcessor> logger) : ILabMessageProcessor
{
    private const string NoSink = "No LIS result sink is registered (the Diagnostics module is not loaded).";

    public async Task<LabSubmitOutcome> SubmitResultsAsync(
        LabInstrumentSettings instrument, IReadOnlyList<InstrumentResultMessage> results, string rawMessage, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var trace = scope.ServiceProvider.GetRequiredService<IntegrationTrace>();
        var started = trace.Start();
        var reference = string.Join(",", results.Select(r => r.SampleBarcode).Distinct(StringComparer.Ordinal).Take(10));

        LabSubmitOutcome outcome;
        var sink = scope.ServiceProvider.GetService<IInstrumentResultSink>();
        if (sink is null)
        {
            logger.LogWarning("Results from instrument {Instrument} refused: {Reason}", instrument.Code, NoSink);
            outcome = new LabSubmitOutcome(false, "LIS not available");
        }
        else
        {
            var errors = new List<string>();
            foreach (var message in results)
            {
                try
                {
                    var result = await sink.ReceiveAsync(message, cancellationToken);
                    if (result.IsFailure)
                    {
                        errors.Add($"{message.SampleBarcode}: {result.Error!.Message}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The LIS could not store results for a sample from instrument {Instrument}", instrument.Code);
                    errors.Add($"{message.SampleBarcode}: could not be stored");
                }
            }

            outcome = errors.Count == 0 ? LabSubmitOutcome.Ok : new LabSubmitOutcome(false, string.Join("; ", errors));
        }

        var resultCount = results.Sum(r => r.Results.Count);
        await trace.LogAsync(IntegrationNames.Lab, Provider(instrument), "results", outcome.Accepted, started, reference,
            LabPhiMasking.Mask(rawMessage, instrument.Protocol), outcome.Accepted ? $"{resultCount} result(s) accepted" : "refused",
            outcome.Accepted ? null : sink is null ? NoSink : outcome.Error, IntegrationDirection.Inbound);
        state.RecordMessage(instrument.Code, outcome.Accepted, outcome.Error);
        return outcome;
    }

    public async Task<HostQueryAnswer> QueryOrdersAsync(
        LabInstrumentSettings instrument, IReadOnlyList<string> sampleIds, string rawMessage, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var trace = scope.ServiceProvider.GetRequiredService<IntegrationTrace>();
        var started = trace.Start();
        var reference = string.Join(",", sampleIds.Take(10));
        var sink = scope.ServiceProvider.GetService<IInstrumentResultSink>();
        HostQueryAnswer answer;
        if (sink is null)
        {
            logger.LogWarning("Host query from instrument {Instrument} refused: {Reason}", instrument.Code, NoSink);
            answer = HostQueryAnswer.Unavailable(NoSink);
        }
        else
        {
            try
            {
                if (LabOrderQueue.OffReason(await sink.GetInstrumentAsync(instrument.Code, cancellationToken), instrument) is { } off)
                {
                    answer = HostQueryAnswer.Off(off);
                }
                else
                {
                    var found = new List<InstrumentOrder>();
                    foreach (var sampleId in sampleIds)
                    {
                        if (await sink.GetOrderForSampleAsync(instrument.Code, sampleId, cancellationToken) is { } order)
                        {
                            found.Add(order);
                        }
                    }

                    answer = HostQueryAnswer.Answer(found);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The LIS could not answer a host query from instrument {Instrument}", instrument.Code);
                answer = HostQueryAnswer.Unavailable("The LIS could not answer the query.");
            }
        }

        var answered = answer.Status == HostQueryStatus.Answered;
        await trace.LogAsync(IntegrationNames.Lab, Provider(instrument), "query", answered, started, reference,
            LabPhiMasking.Mask(rawMessage, instrument.Protocol), answered ? $"{answer.Orders.Count} order(s)" : "refused", answer.Error, IntegrationDirection.Inbound);
        state.RecordMessage(instrument.Code, answered, answer.Error);
        return answer;
    }

    public async Task<IReadOnlyList<LabDownload>> NextDownloadsAsync(LabInstrumentSettings instrument, int max, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sink = scope.ServiceProvider.GetService<IInstrumentResultSink>();
        if (sink is null)
        {
            return [];
        }

        try
        {
            return await scope.ServiceProvider.GetRequiredService<LabOrderQueue>().NextAsync(instrument, sink, max, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database or the LIS is unavailable: the orders stay queued and are looked for again shortly.
            logger.LogWarning(ex, "Orders for instrument {Instrument} could not be read; trying again shortly", instrument.Code);
            return [];
        }
    }

    public async Task ExtendDownloadClaimAsync(
        LabInstrumentSettings instrument, IReadOnlyList<LabDownload> downloads, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<LabOrderQueue>();
        try
        {
            foreach (var claim in downloads.GroupBy(d => d.Claim))
            {
                await queue.ExtendClaimAsync(claim.Select(d => d.Id).ToList(), claim.Key, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database is unavailable: tried again shortly; at worst the claim lapses and the order is sent twice,
            // which analysers accept for the same sample.
            logger.LogWarning(
                ex, "The claim on orders being sent to instrument {Instrument} could not be extended", instrument.Code);
        }
    }

    public async Task CompleteDownloadsAsync(
        LabInstrumentSettings instrument, IReadOnlyList<LabDownload> downloads, bool delivered, string rawMessage, string? error, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var trace = scope.ServiceProvider.GetRequiredService<IntegrationTrace>();
        var operation = instrument.Protocol == LabProtocol.Hl7v2 ? "hl7.download" : "astm.download";
        var reference = string.Join(",", downloads.Select(d => d.Order.SampleBarcode).Distinct(StringComparer.Ordinal).Take(10));
        try
        {
            var queue = scope.ServiceProvider.GetRequiredService<LabOrderQueue>();
            foreach (var claim in downloads.GroupBy(d => d.Claim))
            {
                var ids = claim.Select(d => d.Id).ToList();
                await queue.CompleteAsync(ids, claim.Key, delivered, error, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The claim lapses and the order is sent again: analysers accept a repeated order for the same sample.
            logger.LogError(ex, "The outcome of orders sent to instrument {Instrument} could not be recorded", instrument.Code);
        }

        await trace.LogAsync(IntegrationNames.Lab, Provider(instrument), operation, delivered, trace.Start(), reference,
            LabPhiMasking.Mask(rawMessage, instrument.Protocol), delivered ? $"{downloads.Count} order(s) sent" : null, error, IntegrationDirection.Outbound);
        if (!delivered)
        {
            state.RecordMessage(instrument.Code, false, error);
        }
    }

    public async Task LogAsync(LabInstrumentSettings instrument, string operation, bool success, string rawMessage, string? response, string? error, string? reference)
    {
        await using var scope = scopes.CreateAsyncScope();
        var trace = scope.ServiceProvider.GetRequiredService<IntegrationTrace>();
        var direction = operation.EndsWith("orders", StringComparison.Ordinal) ? IntegrationDirection.Outbound : IntegrationDirection.Inbound;
        await trace.LogAsync(IntegrationNames.Lab, Provider(instrument), operation, success, trace.Start(), reference,
            LabPhiMasking.Mask(rawMessage, instrument.Protocol), response is null ? null : LabPhiMasking.Mask(response, instrument.Protocol), error, direction);
        if (!success)
        {
            state.RecordMessage(instrument.Code, false, error);
        }
    }

    private static string Provider(LabInstrumentSettings instrument) =>
        $"{(instrument.Protocol == LabProtocol.Hl7v2 ? "hl7" : "astm")}:{instrument.Code}";
}
