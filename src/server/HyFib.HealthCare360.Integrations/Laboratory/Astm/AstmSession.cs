using System.Text;

namespace HyFib.HealthCare360.Integrations.Laboratory.Astm;

/// <summary>E1381 timers: receiver frame wait, sender reply wait, NAK back-off and line-contention back-off.</summary>
internal sealed record AstmTimings(TimeSpan FrameTimeout, TimeSpan ReplyTimeout, TimeSpan BusyRetryDelay, TimeSpan ContentionDelay)
{
    public static AstmTimings Standard { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
}

/// <summary>
/// One analyser connection speaking ASTM E1381 (low level) / E1394 (records).
/// <para>
/// Receiving: ENQ is acknowledged; each frame is checked (number, checksum) and answered ACK or NAK; a retransmitted
/// frame after a lost ACK is re-acknowledged without being applied twice. The frame completing a message (the one
/// carrying the L record) is acknowledged only after the LIS has stored the results — if it cannot (LIS unavailable,
/// sample unknown) the frame is NAKed and the analyser retransmits it, so results are never acknowledged and lost.
/// </para>
/// <para>
/// Sending (host query answers): after the analyser's EOT, HC360 bids with ENQ, yields to the analyser if both bid
/// (the instrument has priority), retransmits NAKed frames up to six times and ends with EOT.
/// </para>
/// </summary>
internal sealed class AstmSession(LabInstrumentSettings instrument, LabRuntimeOptions options, ILabMessageProcessor processor, TimeProvider time, ILogger logger)
{
    /// <summary>Compliant frames are at most 247 bytes; some analysers ignore that, so allow more, but bounded.</summary>
    private const int MaxFrameBytes = 64 * 1024;

    private const int MaxSendAttempts = 6;
    private const int MaxEstablishAttempts = 3;

    private readonly byte[] _buffer = new byte[4096];
    private readonly byte[] _single = new byte[1];
    private readonly Queue<IReadOnlyList<string>> _outgoing = new();
    private Stream _stream = Stream.Null;
    private int _bufferStart;
    private int _bufferEnd;

    private enum TokenKind
    {
        Control,
        Frame,
        InvalidFrame,
        Timeout,
        Closed,
    }

    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        _stream = stream;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Neutral state: only ENQ starts a transfer; anything else is line noise and ignored.
            var token = await ReadTokenAsync(options.IdleTimeout, cancellationToken);
            if (token.Kind is TokenKind.Closed or TokenKind.Timeout)
            {
                return;
            }

            if (token.Kind != TokenKind.Control || token.Control != AstmControl.Enq)
            {
                continue;
            }

            await WriteAsync(AstmControl.Ack, cancellationToken);
            await ReceiveAsync(cancellationToken);
            while (_outgoing.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                // One delivery attempt per answer: an analyser that missed it simply queries again.
                var records = _outgoing.Dequeue();
                if (!await SendAsync(records, cancellationToken))
                {
                    break;
                }
            }
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var expected = 1;
        int? lastAccepted = null;
        var records = new List<string>();
        var partial = new StringBuilder();
        var received = 0;
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
                    if (records.Count > 0)
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
                logger.LogWarning("Instrument {Instrument} sent a message larger than {Limit} bytes; discarded", instrument.Code, options.MaxMessageBytes);
                await processor.LogAsync(instrument, "astm.rejected", false, "(message too large)", null, "Message exceeded the size limit.", null);
                records.Clear();
                partial.Clear();
                received = 0;
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
            if (!instrument.HostQuery)
            {
                await processor.LogAsync(instrument, "astm.query", false, raw, null, "Host query is disabled for this instrument.", null);
                return true;
            }

            var sampleIds = AstmMessageMapper.QueriedSampleIds(records);
            var orders = await processor.QueryOrdersAsync(instrument, sampleIds, raw, cancellationToken);
            if (orders is null)
            {
                return false;
            }

            var delimiters = AstmDelimiters.FromHeader(records.FirstOrDefault(r => r.Type == 'H')?.Text);
            _outgoing.Enqueue(AstmMessageMapper.BuildOrderResponse(orders, delimiters, TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Zone)));
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

    private async Task<bool> SendAsync(IReadOnlyList<string> records, CancellationToken cancellationToken)
    {
        var raw = string.Join("\r", records);
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
                    // Both sides bid for the line: the instrument has priority. Receive first, then bid again later.
                    await WriteAsync(AstmControl.Ack, cancellationToken);
                    await ReceiveAsync(cancellationToken);
                    await Task.Delay(options.Timings.ContentionDelay, time, cancellationToken);
                    break;
                case AstmControl.Nak:
                    await Task.Delay(options.Timings.BusyRetryDelay, time, cancellationToken);
                    break;
                default:
                    await WriteAsync(AstmControl.Eot, cancellationToken);
                    await processor.LogAsync(instrument, "astm.orders", false, raw, null, "The analyser did not answer the transfer request.", null);
                    return false;
            }
        }

        if (!established)
        {
            await WriteAsync(AstmControl.Eot, cancellationToken);
            await processor.LogAsync(instrument, "astm.orders", false, raw, null, "The analyser refused the transfer.", null);
            return false;
        }

        foreach (var frame in AstmFrames.BuildMessage(records, options.Encoding))
        {
            byte? reply = null;
            for (var attempt = 0; attempt < MaxSendAttempts; attempt++)
            {
                await _stream.WriteAsync(frame, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
                reply = await ReadControlAsync(options.Timings.ReplyTimeout, cancellationToken);
                if (reply != AstmControl.Nak)
                {
                    break;
                }
            }

            if (reply == AstmControl.Eot)
            {
                // Receiver interrupt: treated as an acknowledgement, and the transfer ends here.
                await WriteAsync(AstmControl.Eot, cancellationToken);
                await processor.LogAsync(instrument, "astm.orders", true, raw, null, "The analyser interrupted the transfer.", null);
                return true;
            }

            if (reply != AstmControl.Ack)
            {
                await WriteAsync(AstmControl.Eot, cancellationToken);
                await processor.LogAsync(instrument, "astm.orders", false, raw, null, "The analyser did not accept the order frames.", null);
                return false;
            }
        }

        await WriteAsync(AstmControl.Eot, cancellationToken);
        await processor.LogAsync(instrument, "astm.orders", true, raw, null, null, null);
        return true;
    }

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

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_bufferStart == _bufferEnd)
        {
            _bufferStart = 0;
            _bufferEnd = await _stream.ReadAsync(_buffer, cancellationToken);
            if (_bufferEnd == 0)
            {
                return -1;
            }
        }

        return _buffer[_bufferStart++];
    }

    private async Task WriteAsync(byte control, CancellationToken cancellationToken)
    {
        _single[0] = control;
        await _stream.WriteAsync(_single, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private readonly record struct Token(TokenKind Kind, byte Control, byte[] Frame);
}
