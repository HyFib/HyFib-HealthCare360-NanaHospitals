using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;

namespace HyFib.HealthCare360.Integrations.Laboratory.Astm;

/// <summary>E1381 timers: receiver frame wait, sender reply wait, NAK back-off and line-contention back-off.</summary>
internal sealed record AstmTimings(TimeSpan FrameTimeout, TimeSpan ReplyTimeout, TimeSpan BusyRetryDelay, TimeSpan ContentionDelay)
{
    public static AstmTimings Standard { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
}

/// <summary>
/// One analyser connection speaking ASTM E1381 (low level) / E1394 (records), over a TCP socket or a lab agent's relay.
/// <para>
/// Receiving: ENQ is acknowledged; each frame is checked (number, checksum) and answered ACK or NAK; a retransmitted
/// frame after a lost ACK is re-acknowledged without being applied twice. The frame completing a message (the one
/// carrying the L record) is acknowledged only after the LIS has stored the results — if it cannot (LIS unavailable,
/// sample unknown) the frame is NAKed and the analyser retransmits it, so results are never acknowledged and lost.
/// </para>
/// <para>
/// Sending (host query answers, and orders sent unasked while the line is idle): HC360 bids with ENQ. If both bid, the
/// instrument has priority (E1381 contention): HC360 does not answer the analyser's colliding ENQ, answers its next one
/// and receives its message, and bids again no sooner than 20 s later. After a NAK (busy) it bids again no sooner than
/// 10 s later. While it waits it keeps listening, so the analyser can always send. NAKed frames are retransmitted up
/// to six times and the message ends with EOT. A receiver interrupt (EOT instead of ACK) acknowledges the frame; the
/// rest of the message is still sent, as E1381 allows, so the analyser never holds half an order.
/// </para>
/// </summary>
internal sealed class AstmSession(LabInstrumentSettings instrument, LabRuntimeOptions options, ILabMessageProcessor processor, TimeProvider time, ILogger logger)
{
    /// <summary>Compliant frames are at most 247 bytes; some analysers ignore that, so allow more, but bounded.</summary>
    private const int MaxFrameBytes = 64 * 1024;

    private const int MaxSendAttempts = 6;
    private const int MaxEstablishAttempts = 3;

    /// <summary>Orders sent unasked go out in messages of at most this many samples.</summary>
    private const int MaxOrdersPerMessage = 10;

    private readonly byte[] _buffer = new byte[4096];
    private readonly byte[] _single = new byte[1];
    private readonly Queue<IReadOnlyList<string>> _outgoing = new();
    private Stream _stream = Stream.Null;
    private CancellationToken _lifetime;
    private Task<int>? _fill;
    private int _bufferStart;
    private int _bufferEnd;
    private long _lastTraffic;

    private enum TokenKind
    {
        Control,
        Frame,
        InvalidFrame,
        Timeout,
        Closed,
    }

    private enum Wake
    {
        Data,
        LookForOrders,
        Idle,
        Closed,
    }

    private readonly record struct SendOutcome(bool Delivered, string? Error);

    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        _stream = stream;
        _lifetime = cancellationToken;
        _lastTraffic = time.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            // Neutral state: the analyser may bid (ENQ); otherwise, while the line is quiet, queued orders are sent.
            switch (await WaitInNeutralAsync(cancellationToken))
            {
                case Wake.Closed or Wake.Idle:
                    return;
                case Wake.LookForOrders:
                    await SendQueuedOrdersAsync(cancellationToken);
                    await SendAnswersAsync(cancellationToken);
                    continue;
            }

            var token = await ReadTokenAsync(options.Timings.FrameTimeout, cancellationToken);
            if (token.Kind == TokenKind.Closed)
            {
                return;
            }

            if (token.Kind != TokenKind.Control || token.Control != AstmControl.Enq)
            {
                // Only ENQ starts a transfer; anything else is line noise and ignored.
                continue;
            }

            await WriteAsync(AstmControl.Ack, cancellationToken);
            await ReceiveAsync(cancellationToken);
            await SendAnswersAsync(cancellationToken);
        }
    }

    /// <summary>Host query answers, one delivery attempt each: an analyser that missed one simply queries again.</summary>
    private async Task SendAnswersAsync(CancellationToken cancellationToken)
    {
        while (_outgoing.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var records = _outgoing.Dequeue();
            var outcome = await SendAsync(records, cancellationToken);
            await processor.LogAsync(instrument, "astm.orders", outcome.Delivered, string.Join("\r", records), null, outcome.Error, null);
            if (!outcome.Delivered)
            {
                _outgoing.Clear();
                return;
            }
        }
    }

    /// <summary>Orders queued for this analyser (new samples, reruns, cancellations), sent while the line is idle.</summary>
    private async Task SendQueuedOrdersAsync(CancellationToken cancellationToken)
    {
        var downloads = await processor.NextDownloadsAsync(instrument, MaxOrdersPerMessage, cancellationToken);
        if (downloads.Count == 0)
        {
            return;
        }

        var records = AstmMessageMapper.BuildOrderDownload(downloads, AstmDelimiters.Default, TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Zone), options.Zone);
        var outcome = await SendAsync(records, cancellationToken);
        await processor.CompleteDownloadsAsync(instrument, downloads, outcome.Delivered, string.Join("\r", records), outcome.Error, cancellationToken);
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var expected = 1;
        int? lastAccepted = null;
        var records = new List<string>();
        var partial = new StringBuilder();
        var received = 0;
        var tooLarge = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var token = await ReadTokenAsync(options.Timings.FrameTimeout, cancellationToken);
            switch (token.Kind)
            {
                case TokenKind.Closed:
                    return;
                case TokenKind.Timeout:
                    logger.LogWarning("Instrument {Instrument} stopped sending mid-message; the incomplete message was discarded", instrument.Code);
                    return;
                case TokenKind.Control when token.Control == AstmControl.Eot:
                    if (records.Count > 0 && !tooLarge)
                    {
                        // Sender ended without a terminator record: best effort, it can no longer be NAKed.
                        await ProcessAsync(records, cancellationToken);
                    }

                    return;
                case TokenKind.Control:
                    continue;
                case TokenKind.InvalidFrame:
                    await WriteAsync(AstmControl.Nak, cancellationToken);
                    continue;
            }

            if (tooLarge)
            {
                // The transfer is abandoned: every frame is refused until the analyser gives up and sends EOT.
                await WriteAsync(AstmControl.Nak, cancellationToken);
                continue;
            }

            if (!AstmFrames.TryParse(token.Frame, options.Encoding, out var frame, out var error))
            {
                logger.LogDebug("Instrument {Instrument} sent an invalid frame ({Error}); NAK", instrument.Code, error);
                await WriteAsync(AstmControl.Nak, cancellationToken);
                continue;
            }

            if (frame.FrameNumber == lastAccepted)
            {
                // Our ACK was lost and the sender repeated the frame: acknowledge again, do not apply it twice.
                await WriteAsync(AstmControl.Ack, cancellationToken);
                continue;
            }

            if (frame.FrameNumber != expected)
            {
                await WriteAsync(AstmControl.Nak, cancellationToken);
                continue;
            }

            if (received + frame.Text.Length > options.MaxMessageBytes)
            {
                // Nothing of this transfer is stored, not even frames already acknowledged: storing the tail of a
                // message without its start would attach results to the wrong sample.
                logger.LogWarning(
                    "Instrument {Instrument} sent a message larger than {Limit} bytes; the transfer was refused",
                    instrument.Code,
                    options.MaxMessageBytes);
                await processor.LogAsync(instrument, "astm.rejected", false, "(message too large)", null, "Message exceeded the size limit.", null);
                tooLarge = true;
                records.Clear();
                partial.Clear();
                await WriteAsync(AstmControl.Nak, cancellationToken);
                continue;
            }

            if (frame.IsFinal)
            {
                var completed = (partial.ToString() + frame.Text)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0)
                    .ToList();
                var candidate = records.Concat(completed).ToList();
                if (completed.Any(r => char.ToUpperInvariant(r[0]) == 'L'))
                {
                    if (!await ProcessAsync(candidate, cancellationToken))
                    {
                        // State unchanged: the analyser retransmits this frame and it is processed again.
                        await WriteAsync(AstmControl.Nak, cancellationToken);
                        continue;
                    }

                    records.Clear();
                    received = 0;
                }
                else
                {
                    records = candidate;
                    received += frame.Text.Length;
                }

                partial.Clear();
            }
            else
            {
                partial.Append(frame.Text);
                received += frame.Text.Length;
            }

            lastAccepted = expected;
            expected = (expected + 1) % 8;
            await WriteAsync(AstmControl.Ack, cancellationToken);
        }
    }

    /// <summary>Applies one complete message. False means "not stored — NAK so the analyser retries".</summary>
    private async Task<bool> ProcessAsync(IReadOnlyList<string> recordTexts, CancellationToken cancellationToken)
    {
        var raw = string.Join("\r", recordTexts);
        var records = AstmRecords.Parse(raw);
        if (records.Any(r => r.Type == 'Q'))
        {
            var sampleIds = AstmMessageMapper.QueriedSampleIds(records);
            var answer = await processor.QueryOrdersAsync(instrument, sampleIds, raw, cancellationToken);
            switch (answer.Status)
            {
                case HostQueryStatus.Unavailable:
                    return false;
                case HostQueryStatus.TurnedOff:
                    // Acknowledged and logged with the reason; the analyser gets no answer, as with no host query at all.
                    return true;
            }

            var delimiters = AstmDelimiters.FromHeader(records.FirstOrDefault(r => r.Type == 'H')?.Text);
            _outgoing.Enqueue(AstmMessageMapper.BuildOrderResponse(answer.Orders, delimiters, TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Zone), options.Zone));
            return true;
        }

        var results = AstmMessageMapper.MapResults(records, instrument.Code, raw, time.GetUtcNow(), options.Zone);
        if (results.Count == 0)
        {
            // Keep-alives, order echoes and results the analyser could not produce (status X) carry nothing to store.
            await processor.LogAsync(instrument, "astm.message", true, raw, null, null, null);
            return true;
        }

        var outcome = await processor.SubmitResultsAsync(instrument, results, raw, cancellationToken);
        return outcome.Accepted;
    }

    private async Task<SendOutcome> SendAsync(IReadOnlyList<string> records, CancellationToken cancellationToken)
    {
        var established = false;
        for (var attempt = 0; attempt < MaxEstablishAttempts && !established; attempt++)
        {
            await WriteAsync(AstmControl.Enq, cancellationToken);
            switch (await ReadControlAsync(options.Timings.ReplyTimeout, cancellationToken))
            {
                case AstmControl.Ack:
                    established = true;
                    break;
                case AstmControl.Enq:
                    // Contention: both sides bid for the line and the instrument has priority. Its colliding ENQ is not
                    // answered; it bids again shortly and that ENQ is answered, while HC360 waits before bidding again.
                    if (!await WaitBeforeBiddingAsync(options.Timings.ContentionDelay, cancellationToken))
                    {
                        return new SendOutcome(false, "The connection closed.");
                    }

                    break;
                case AstmControl.Nak:
                    if (!await WaitBeforeBiddingAsync(options.Timings.BusyRetryDelay, cancellationToken))
                    {
                        return new SendOutcome(false, "The connection closed.");
                    }

                    break;
                default:
                    await WriteAsync(AstmControl.Eot, cancellationToken);
                    return new SendOutcome(false, "The analyser did not answer the transfer request.");
            }
        }

        if (!established)
        {
            await WriteAsync(AstmControl.Eot, cancellationToken);
            return new SendOutcome(false, "The analyser refused the transfer.");
        }

        foreach (var frame in AstmFrames.BuildMessage(records, options.Encoding))
        {
            byte? reply = null;
            for (var attempt = 0; attempt < MaxSendAttempts; attempt++)
            {
                await _stream.WriteAsync(frame, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
                _lastTraffic = time.GetTimestamp();
                reply = await ReadControlAsync(options.Timings.ReplyTimeout, cancellationToken);
                if (reply != AstmControl.Nak)
                {
                    break;
                }
            }

            // EOT is a receiver interrupt: it acknowledges this frame, and the sender may finish the message.
            if (reply is not (AstmControl.Ack or AstmControl.Eot))
            {
                await WriteAsync(AstmControl.Eot, cancellationToken);
                return new SendOutcome(false, "The analyser did not accept the order frames.");
            }
        }

        await WriteAsync(AstmControl.Eot, cancellationToken);
        return new SendOutcome(true, null);
    }

    /// <summary>
    /// Waits before bidding for the line again (after contention or a busy NAK) without leaving it: the line is in the
    /// neutral state, so an analyser that bids meanwhile is answered and its message received as usual. False when the
    /// connection closed.
    /// </summary>
    private async Task<bool> WaitBeforeBiddingAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            var left = wait - time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
            {
                return true;
            }

            var token = await ReadTokenAsync(left, cancellationToken);
            switch (token.Kind)
            {
                case TokenKind.Closed:
                    return false;
                case TokenKind.Control when token.Control == AstmControl.Enq:
                    await WriteAsync(AstmControl.Ack, cancellationToken);
                    await ReceiveAsync(cancellationToken);
                    break;
            }
        }
    }

    /// <summary>
    /// Waits in the neutral state for the analyser's next byte. Every <see cref="LabRuntimeOptions.OrderPollInterval"/>
    /// of quiet it returns to look for queued orders, and after <see cref="LabRuntimeOptions.IdleTimeout"/> without any
    /// traffic it gives up the connection. The read it starts stays pending across these waits, so no byte is lost.
    /// </summary>
    private async Task<Wake> WaitInNeutralAsync(CancellationToken cancellationToken)
    {
        if (_bufferStart < _bufferEnd)
        {
            return Wake.Data;
        }

        var idleLeft = IdleLeft();
        if (idleLeft <= TimeSpan.Zero)
        {
            return Wake.Idle;
        }

        var fill = _fill ??= _stream.ReadAsync(_buffer, _lifetime).AsTask();
        var wait = idleLeft is { } left && left < options.OrderPollInterval ? left : options.OrderPollInterval;
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(wait, time, quiet.Token);
        var first = await Task.WhenAny(fill, delay);
        await quiet.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (first == fill)
        {
            return await TakeFillAsync() == 0 ? Wake.Closed : Wake.Data;
        }

        return IdleLeft() <= TimeSpan.Zero ? Wake.Idle : Wake.LookForOrders;
    }

    /// <summary>
    /// Time left before the connection counts as idle; null when it never does (a relayed line, whose idle timeout is
    /// <see cref="Timeout.InfiniteTimeSpan"/>). Null never compares as less than zero.
    /// </summary>
    private TimeSpan? IdleLeft() =>
        options.IdleTimeout == Timeout.InfiniteTimeSpan ? null : options.IdleTimeout - time.GetElapsedTime(_lastTraffic);

    private async Task<byte?> ReadControlAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        while (true)
        {
            var token = await ReadTokenAsync(timeout, cancellationToken);
            switch (token.Kind)
            {
                case TokenKind.Control:
                    return token.Control;
                case TokenKind.Timeout:
                case TokenKind.Closed:
                    return null;
            }
        }
    }

    private async Task<Token> ReadTokenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var value = await ReadByteAsync(deadline.Token);
                switch (value)
                {
                    case < 0:
                        return new Token(TokenKind.Closed, 0, []);
                    case AstmControl.Enq or AstmControl.Eot or AstmControl.Ack or AstmControl.Nak:
                        return new Token(TokenKind.Control, (byte)value, []);
                    case AstmControl.Stx:
                        return await ReadFrameAsync(deadline.Token);
                }

                // Anything else between frames (stray CR/LF, NUL) is ignored.
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Token(TokenKind.Timeout, 0, []);
        }
    }

    private async Task<Token> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var frame = new List<byte> { AstmControl.Stx };
        while (true)
        {
            var value = await ReadByteAsync(cancellationToken);
            if (value < 0)
            {
                return new Token(TokenKind.Closed, 0, []);
            }

            switch (value)
            {
                case AstmControl.Stx:
                    // The sender restarted the frame.
                    frame.Clear();
                    frame.Add(AstmControl.Stx);
                    continue;
                case AstmControl.Eot or AstmControl.Enq:
                    // The sender gave up mid-frame.
                    return new Token(TokenKind.Control, (byte)value, []);
            }

            frame.Add((byte)value);
            if (value == AstmControl.Lf && frame.Count >= 3 && frame[^2] == AstmControl.Cr)
            {
                return new Token(TokenKind.Frame, 0, [.. frame]);
            }

            if (frame.Count > MaxFrameBytes)
            {
                return new Token(TokenKind.InvalidFrame, 0, []);
            }
        }
    }

    /// <summary>
    /// One byte, or -1 at end of stream. A timeout abandons the wait, not the read: the pending read is kept for the next
    /// call, so a slow line never loses bytes and the transport (a relay's WebSocket included) is never cancelled.
    /// </summary>
    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_bufferStart == _bufferEnd)
        {
            _fill ??= _stream.ReadAsync(_buffer, _lifetime).AsTask();
            await _fill.WaitAsync(cancellationToken);
            if (await TakeFillAsync() == 0)
            {
                return -1;
            }
        }

        return _buffer[_bufferStart++];
    }

    private async Task<int> TakeFillAsync()
    {
        var fill = _fill!;
        _fill = null;
        _bufferStart = 0;
        _bufferEnd = await fill;
        if (_bufferEnd > 0)
        {
            _lastTraffic = time.GetTimestamp();
        }

        return _bufferEnd;
    }

    private async Task WriteAsync(byte control, CancellationToken cancellationToken)
    {
        _single[0] = control;
        await _stream.WriteAsync(_single, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
        _lastTraffic = time.GetTimestamp();
    }

    private readonly record struct Token(TokenKind Kind, byte Control, byte[] Frame);
}
