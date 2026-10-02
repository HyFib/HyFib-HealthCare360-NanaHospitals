namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>
/// One analyser connection speaking HL7 v2 over MLLP, over a TCP socket or a lab agent's relay.
/// <list type="bullet">
/// <item>Every message the analyser sends gets exactly one framed answer: an ACK for results, RSP^K11 for a QBP^Q11 host
/// query, QCK^Q02 for a QRY^Q02 (its orders follow as order messages). Acknowledgements are never acknowledged.</item>
/// <item>While the connection is quiet, orders queued for the analyser are sent one sample per message (OML^O21 or
/// ORM^O01), each waiting for the analyser's acknowledgement before the next; results that arrive meanwhile are handled
/// as usual.</item>
/// <item>The connection is closed when it stays idle past the configured timeout or a message exceeds the size limit.</item>
/// </list>
/// </summary>
internal sealed class MllpSession(LabInstrumentSettings instrument, LabRuntimeOptions options, ILabMessageProcessor processor, TimeProvider time, ILogger logger)
{
    /// <summary>Orders sent per quiet spell, so results never wait long behind a large backlog.</summary>
    private const int MaxOrdersPerTurn = 10;

    private readonly byte[] _buffer = new byte[8192];
    private readonly Queue<string> _inbound = new();
    private readonly Queue<(InstrumentOrder Order, string MessageType)> _answers = new();
    private MllpFrameReader _reader = new(options.MaxMessageBytes);
    private Stream _stream = Stream.Null;
    private CancellationToken _lifetime;
    private Task<int>? _fill;
    private long _lastTraffic;

    private enum Wake
    {
        Data,
        LookForOrders,
        TimedOut,
        Idle,
        Closed,
        Overflow,
    }

    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        _stream = stream;
        _lifetime = cancellationToken;
        _reader = new MllpFrameReader(options.MaxMessageBytes);
        _lastTraffic = time.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_inbound.TryDequeue(out var text))
            {
                await RespondAsync(text, cancellationToken);
                await SendAnswersAsync(cancellationToken);
                continue;
            }

            switch (await WaitAsync(Timeout.InfiniteTimeSpan, pollForOrders: true, cancellationToken))
            {
                case Wake.Closed or Wake.Idle:
                    return;
                case Wake.Overflow:
                    // Messages completed before the oversized one are still answered.
                    while (_inbound.TryDequeue(out var complete))
                    {
                        await RespondAsync(complete, cancellationToken);
                    }

                    await RefuseOversizedAsync();
                    return;
                case Wake.LookForOrders:
                    await SendQueuedOrdersAsync(cancellationToken);
                    await SendAnswersAsync(cancellationToken);
                    break;
            }
        }
    }

    /// <summary>Processes one decoded message and returns the answer to send, or null for an acknowledgement.</summary>
    internal async Task<string?> HandleAsync(string text, CancellationToken cancellationToken)
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Zone);
        var controlId = Hl7Ack.NewControlId();
        if (!Hl7Message.TryParse(text, out var message, out var parseError))
        {
            var reject = Hl7Ack.Build(null, Hl7Ack.Reject, parseError, controlId, now);
            await processor.LogAsync(instrument, "hl7.rejected", false, text, reject, parseError, null);
            return reject;
        }

        if (Hl7Queries.IsQbp(message))
        {
            return await AnswerQbpAsync(message, text, controlId, now, cancellationToken);
        }

        if (Hl7Queries.IsQry(message))
        {
            return await AnswerQryAsync(message, text, controlId, now, cancellationToken);
        }

        if (Hl7Queries.IsAcknowledgement(message))
        {
            // An acknowledgement nobody is waiting for (a late one, or a duplicate): logged, never acknowledged.
            await processor.LogAsync(instrument, "hl7.ack", true, text, null, null, message.ControlId);
            return null;
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

    private async Task<string> AnswerQbpAsync(Hl7Message query, string text, string controlId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Hl7Queries.QbpSampleId(query) is not { } sampleId)
        {
            const string missing = "The query names no sample (QPD-3).";
            await processor.LogAsync(instrument, "hl7.query", false, text, null, missing, query.ControlId);
            return Hl7Queries.BuildRsp(query, Hl7Ack.Error, "AE", missing, [], controlId, now, options.Zone);
        }

        var answer = await processor.QueryOrdersAsync(instrument, [sampleId], text, cancellationToken);
        return answer.Status switch
        {
            HostQueryStatus.Unavailable => Hl7Queries.BuildRsp(query, Hl7Ack.Error, "AE", answer.Error, [], controlId, now, options.Zone),
            HostQueryStatus.TurnedOff => Hl7Queries.BuildRsp(query, Hl7Ack.Reject, "AR", "Host query is not enabled for this analyser.", [], controlId, now, options.Zone),
            _ when answer.Orders.Any(o => o.ChannelCodes.Count > 0) => Hl7Queries.BuildRsp(query, Hl7Ack.Accept, "OK", null, answer.Orders, controlId, now, options.Zone),
            _ => Hl7Queries.BuildRsp(query, Hl7Ack.Accept, "NF", null, [], controlId, now, options.Zone),
        };
    }

    private async Task<string> AnswerQryAsync(Hl7Message query, string text, string controlId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Hl7Queries.QrySampleId(query) is not { } sampleId)
        {
            const string missing = "The query names no sample (QRD-8).";
            await processor.LogAsync(instrument, "hl7.query", false, text, null, missing, query.ControlId);
            return Hl7Queries.BuildQck(query, Hl7Ack.Error, "AE", missing, controlId, now);
        }

        var answer = await processor.QueryOrdersAsync(instrument, [sampleId], text, cancellationToken);
        switch (answer.Status)
        {
            case HostQueryStatus.Unavailable:
                return Hl7Queries.BuildQck(query, Hl7Ack.Error, "AE", answer.Error, controlId, now);
            case HostQueryStatus.TurnedOff:
                return Hl7Queries.BuildQck(query, Hl7Ack.Reject, "AR", "Host query is not enabled for this analyser.", controlId, now);
        }

        var orders = answer.Orders.Where(o => o.ChannelCodes.Count > 0).ToList();
        var messageType = Hl7OrderMessages.MessageFor(instrument, query.Version);
        foreach (var order in orders)
        {
            _answers.Enqueue((order, messageType));
        }

        return Hl7Queries.BuildQck(query, Hl7Ack.Accept, orders.Count > 0 ? "OK" : "NF", null, controlId, now);
    }

    private async Task RespondAsync(string text, CancellationToken cancellationToken)
    {
        if (await HandleAsync(text, cancellationToken) is { } reply)
        {
            await WriteAsync(reply, cancellationToken);
        }
    }

    /// <summary>Orders found by a QRY^Q02, sent after its QCK. One attempt each: the analyser can query again.</summary>
    private async Task SendAnswersAsync(CancellationToken cancellationToken)
    {
        while (_answers.TryDequeue(out var answer) && !cancellationToken.IsCancellationRequested)
        {
            var (delivered, raw, error) = await SendOrderAsync(answer.Order, InstrumentOrderAction.New, answer.MessageType, cancellationToken);
            await processor.LogAsync(instrument, "hl7.orders", delivered, raw, null, error, answer.Order.SampleBarcode);
            if (!delivered)
            {
                _answers.Clear();
            }
        }
    }

    /// <summary>Orders queued for this analyser, one sample per message, while the connection is quiet.</summary>
    private async Task SendQueuedOrdersAsync(CancellationToken cancellationToken)
    {
        var messageType = Hl7OrderMessages.MessageFor(instrument);
        for (var sent = 0; sent < MaxOrdersPerTurn && !cancellationToken.IsCancellationRequested; sent++)
        {
            var downloads = await processor.NextDownloadsAsync(instrument, 1, cancellationToken);
            if (downloads.Count == 0)
            {
                return;
            }

            var download = downloads[0];
            var (delivered, raw, error) = await SendOrderAsync(download.Order, download.Action, messageType, cancellationToken);
            await processor.CompleteDownloadsAsync(instrument, downloads, delivered, raw, error, cancellationToken);
            if (!delivered)
            {
                return;
            }
        }
    }

    /// <summary>Sends one order message and waits for the acknowledgement that names its control id.</summary>
    private async Task<(bool Delivered, string Raw, string? Error)> SendOrderAsync(
        InstrumentOrder order, InstrumentOrderAction action, string messageType, CancellationToken cancellationToken)
    {
        var controlId = Hl7Ack.NewControlId();
        var raw = Hl7OrderMessages.Build(order, action, messageType, instrument.Code, controlId, TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Zone), options.Zone);
        await WriteAsync(raw, cancellationToken);
        var started = time.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_inbound.TryDequeue(out var text))
            {
                if (Hl7Message.TryParse(text, out var message, out _) && Hl7Queries.IsAcknowledgement(message)
                    && Hl7Queries.Segment(message, "MSA") is { } msa && msa.Value(2) == controlId)
                {
                    var code = msa.Component(1, 1);
                    return code is "AA" or "CA"
                        ? (true, raw, null)
                        : (false, raw, $"The analyser refused the order ({code}): {IntegrationTrace.Truncate(msa.Value(3), 200)}");
                }

                // Results (or a query) arriving meanwhile are answered as usual.
                await RespondAsync(text, cancellationToken);
            }

            var left = options.Hl7ReplyTimeout - time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            switch (await WaitAsync(left, pollForOrders: false, cancellationToken))
            {
                case Wake.Closed or Wake.Idle:
                    return (false, raw, "The connection closed before the analyser acknowledged the order.");
                case Wake.Overflow:
                    await RefuseOversizedAsync();
                    return (false, raw, "The analyser sent a message larger than the limit.");
            }
        }

        return (false, raw, "The analyser did not acknowledge the order.");
    }

    /// <summary>
    /// Reads until a whole message is decoded (Data), the connection ends, it stays idle past the idle timeout, the
    /// order poll interval passes (LookForOrders, when <paramref name="pollForOrders"/>) or <paramref name="limit"/>
    /// passes (TimedOut).
    /// The read stays pending across these waits, so no byte is lost and the transport is never cancelled.
    /// </summary>
    private async Task<Wake> WaitAsync(TimeSpan limit, bool pollForOrders, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            // Null: a relayed line (idle timeout Timeout.InfiniteTimeSpan, which is −1 ms) never counts as idle.
            TimeSpan? idleLeft = options.IdleTimeout == Timeout.InfiniteTimeSpan ? null : options.IdleTimeout - time.GetElapsedTime(_lastTraffic);
            if (idleLeft <= TimeSpan.Zero)
            {
                logger.LogDebug("Closing idle HL7 connection from instrument {Instrument}", instrument.Code);
                return Wake.Idle;
            }

            TimeSpan? limitLeft = limit == Timeout.InfiniteTimeSpan ? null : limit - time.GetElapsedTime(started);
            if (limitLeft <= TimeSpan.Zero)
            {
                return Wake.TimedOut;
            }

            TimeSpan? poll = pollForOrders ? options.OrderPollInterval : null;
            var wait = Shortest(idleLeft, limitLeft, poll);
            var fill = _fill ??= _stream.ReadAsync(_buffer, _lifetime).AsTask();
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(wait, time, quiet.Token);
            var first = await Task.WhenAny(fill, delay);
            await quiet.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (first != fill)
            {
                if (poll == wait)
                {
                    return Wake.LookForOrders;
                }

                continue;
            }

            _fill = null;
            var read = await fill;
            if (read == 0)
            {
                return Wake.Closed;
            }

            _lastTraffic = time.GetTimestamp();
            foreach (var payload in _reader.Append(_buffer.AsSpan(0, read)))
            {
                _inbound.Enqueue(options.Encoding.GetString(payload));
            }

            if (_reader.Overflowed)
            {
                return Wake.Overflow;
            }

            if (_inbound.Count > 0)
            {
                return Wake.Data;
            }
        }
    }

    private async Task RefuseOversizedAsync()
    {
        var error = $"A message exceeded the {options.MaxMessageBytes / 1024} KB limit; connection closed.";
        logger.LogWarning("Instrument {Instrument}: {Error}", instrument.Code, error);
        await processor.LogAsync(instrument, "hl7.rejected", false, "(message too large)", null, error, null);
    }

    private async Task WriteAsync(string message, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(Mllp.Frame(options.Encoding.GetBytes(message)), cancellationToken);
        await _stream.FlushAsync(cancellationToken);
        _lastTraffic = time.GetTimestamp();
    }

    /// <summary>The shortest of the waits that apply (null: no limit), or <see cref="Timeout.InfiniteTimeSpan"/> when none does.</summary>
    private static TimeSpan Shortest(params TimeSpan?[] spans)
    {
        var finite = spans.OfType<TimeSpan>().ToList();
        return finite.Count == 0 ? Timeout.InfiniteTimeSpan : finite.Min();
    }
}
