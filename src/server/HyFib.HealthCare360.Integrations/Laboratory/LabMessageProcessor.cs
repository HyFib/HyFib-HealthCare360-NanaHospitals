namespace HyFib.HealthCare360.Integrations.Laboratory;

internal sealed record LabSubmitOutcome(bool Accepted, string? Error)
{
    public static LabSubmitOutcome Ok { get; } = new(true, null);
}

/// <summary>What the protocol sessions need from the rest of HC360: the LIS (through the platform contract) and logging.</summary>
internal interface ILabMessageProcessor
{
    /// <summary>Hands decoded results to the LIS. Not accepted when no LIS is registered or it rejects a sample.</summary>
    Task<LabSubmitOutcome> SubmitResultsAsync(LabInstrumentSettings instrument, IReadOnlyList<InstrumentResultMessage> results, string rawMessage, CancellationToken cancellationToken);

    /// <summary>Orders for queried samples (host query), or null when the LIS is unavailable.</summary>
    Task<IReadOnlyList<InstrumentOrder>?> QueryOrdersAsync(LabInstrumentSettings instrument, IReadOnlyList<string> sampleIds, string rawMessage, CancellationToken cancellationToken);

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

    public async Task<IReadOnlyList<InstrumentOrder>?> QueryOrdersAsync(
        LabInstrumentSettings instrument, IReadOnlyList<string> sampleIds, string rawMessage, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var trace = scope.ServiceProvider.GetRequiredService<IntegrationTrace>();
        var started = trace.Start();
        var reference = string.Join(",", sampleIds.Take(10));
        var sink = scope.ServiceProvider.GetService<IInstrumentResultSink>();
        IReadOnlyList<InstrumentOrder>? orders = null;
        string? error = null;
        if (sink is null)
        {
            error = NoSink;
            logger.LogWarning("Host query from instrument {Instrument} refused: {Reason}", instrument.Code, NoSink);
        }
        else
        {
            try
            {
                var found = new List<InstrumentOrder>();
                foreach (var sampleId in sampleIds)
                {
                    if (await sink.GetOrderForSampleAsync(instrument.Code, sampleId, cancellationToken) is { } order)
                    {
                        found.Add(order);
                    }
                }

                orders = found;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The LIS could not answer a host query from instrument {Instrument}", instrument.Code);
                error = "The LIS could not answer the query.";
            }
        }

        await trace.LogAsync(IntegrationNames.Lab, Provider(instrument), "query", orders is not null, started, reference,
            LabPhiMasking.Mask(rawMessage, instrument.Protocol), orders is null ? "refused" : $"{orders.Count} order(s)", error, IntegrationDirection.Inbound);
        state.RecordMessage(instrument.Code, orders is not null, error);
        return orders;
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
