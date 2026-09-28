namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>
/// One analyser connection speaking HL7 v2 over MLLP: every complete message gets exactly one framed ACK. The
/// connection is closed when it stays idle past the configured timeout or a message exceeds the size limit.
/// </summary>
internal sealed class MllpSession(LabInstrumentSettings instrument, LabRuntimeOptions options, ILabMessageProcessor processor, TimeProvider time, ILogger logger)
{
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new MllpFrameReader(options.MaxMessageBytes);
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(options.IdleTimeout);
                try
                {
                    read = await stream.ReadAsync(buffer, idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug("Closing idle HL7 connection from instrument {Instrument}", instrument.Code);
                    return;
                }
            }

            if (read == 0)
            {
                return;
            }

            foreach (var payload in reader.Append(buffer.AsSpan(0, read)))
            {
                var acknowledgement = await HandleAsync(options.Encoding.GetString(payload), cancellationToken);
                await stream.WriteAsync(Mllp.Frame(options.Encoding.GetBytes(acknowledgement)), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (reader.Overflowed)
            {
                var error = $"A message exceeded the {options.MaxMessageBytes / 1024} KB limit; connection closed.";
                logger.LogWarning("Instrument {Instrument}: {Error}", instrument.Code, error);
                await processor.LogAsync(instrument, "hl7.rejected", false, "(message too large)", null, error, null);
                return;
            }
        }
    }

    /// <summary>Processes one decoded message and returns the ACK to send.</summary>
    internal async Task<string> HandleAsync(string text, CancellationToken cancellationToken)
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Zone);
        var controlId = Hl7Ack.NewControlId();
        if (!Hl7Message.TryParse(text, out var message, out var parseError))
        {
            var reject = Hl7Ack.Build(null, Hl7Ack.Reject, parseError, controlId, now);
            await processor.LogAsync(instrument, "hl7.rejected", false, text, reject, parseError, null);
            return reject;
        }

        if (!Hl7ResultMapper.IsResultMessage(message))
        {
            var error = $"Unsupported message type {message.MessageType}^{message.TriggerEvent}.";
            var reject = Hl7Ack.Build(message, Hl7Ack.Reject, error, controlId, now);
            await processor.LogAsync(instrument, "hl7.rejected", false, text, reject, error, message.ControlId);
            return reject;
        }

        var mapped = Hl7ResultMapper.Map(message, instrument.Code, instrument.SampleIdFields, text, time.GetUtcNow(), options.Zone);
        if (mapped.Error is { } mappingError)
        {
            var failure = Hl7Ack.Build(message, Hl7Ack.Error, mappingError, controlId, now);
            await processor.LogAsync(instrument, "hl7.results", false, text, failure, mappingError, message.ControlId);
            return failure;
        }

        var outcome = await processor.SubmitResultsAsync(instrument, mapped.Messages, text, cancellationToken);
        return Hl7Ack.Build(message, outcome.Accepted ? Hl7Ack.Accept : Hl7Ack.Error, outcome.Accepted ? null : outcome.Error, controlId, now);
    }
}
