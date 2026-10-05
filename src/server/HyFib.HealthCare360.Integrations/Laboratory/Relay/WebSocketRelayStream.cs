using System.IO.Pipelines;
using System.Net.WebSockets;

namespace HyFib.HealthCare360.Integrations.Laboratory.Relay;

/// <summary>
/// A serial analyser's line as the lab agent relays it: the bytes travel in binary WebSocket messages both ways, and
/// this stream hands them to the unchanged protocol session.
/// <list type="bullet">
/// <item>A pump receives into a pipe, so a session's read timeout only abandons a wait. Cancelling a WebSocket receive
/// would abort the socket.</item>
/// <item>Writes are sent at once, one at a time, as one binary message each.</item>
/// <item>A text message is a protocol error: the relay carries bytes only, and the agent never answers the analyser
/// itself.</item>
/// <item>The stream ends when the agent closes the socket or the connection drops. The server ends it with
/// <see cref="CloseAsync"/>, which tells the agent why in a close frame; cancelling a read or a write never aborts the
/// socket.</item>
/// </list>
/// </summary>
internal sealed class WebSocketRelayStream : Stream
{
    private readonly WebSocket _socket;
    private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024, useSynchronizationContext: false));
    private readonly Stream _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private long _received;
    private long _sent;
    private bool _disposed;

    public WebSocketRelayStream(WebSocket socket)
    {
        _socket = socket;
        _reader = _incoming.Reader.AsStream();
        _pump = PumpAsync(_stopping.Token);
    }

    /// <summary>Bytes relayed from the analyser and to it (counts only; the content is never logged).</summary>
    public (long Received, long Sent) Traffic => (Interlocked.Read(ref _received), Interlocked.Read(ref _sent));

    /// <summary>Why the relay ended when the agent broke the rules, otherwise null.</summary>
    public string? ProtocolError { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The relay is asynchronous.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The relay is asynchronous.");

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new IOException("The lab agent's relay is closed.");
            }

            // A started send finishes even if the session is cancelled meanwhile: a cancelled send aborts the socket.
            await _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, _stopping.Token);
            Interlocked.Add(ref _sent, buffer.Length);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            throw new IOException("The lab agent's relay was lost.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Sends a close frame saying why the relay ends and waits briefly for the agent's own close frame; an agent that
    /// does not answer in time is cut off.
    /// </summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string description)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(status, description, timeout.Token);
                }
            }
            finally
            {
                _sendLock.Release();
            }

            // The agent answers with its own close frame, which ends the pump.
            await _pump.WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is already gone, or the agent did not answer in time.
        }

        await _stopping.CancelAsync();
        await _pump;
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _stopping.CancelAsync();
            await _pump;
        }

        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _stopping.Cancel();
            _stopping.Dispose();
            _sendLock.Dispose();
            _reader.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var writer = _incoming.Writer;
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(4096);
                var result = await _socket.ReceiveAsync(memory, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    ProtocolError = "The lab agent sent text on the relay; only the analyser's bytes may be relayed.";
                    break;
                }

                writer.Advance(result.Count);
                Interlocked.Add(ref _received, result.Count);
                var flushed = await writer.FlushAsync(cancellationToken);
                if (flushed.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The agent went away, the relay was replaced or the server is stopping: the session reads end of stream.
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }
}
