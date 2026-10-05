using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>Opens the relay WebSocket for one analyser.</summary>
internal interface IRelayConnector
{
    Task<WebSocket> ConnectAsync(string instrumentCode, CancellationToken cancellationToken);
}

/// <summary>
/// The relay's WebSocket: <c>wss://…/api/v1/integrations/lab/agent/relay/{code}</c> with the device credential, the
/// relay sub-protocol, the pinned hospital CA and keep-alive pings, so a dead link is noticed within a minute.
/// </summary>
internal sealed class RelayConnector(Uri serverUri, ServerTrust trust, Guid deviceId, string secret) : IRelayConnector
{
    public async Task<WebSocket> ConnectAsync(string instrumentCode, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader(AgentProtocol.DeviceIdHeader, deviceId.ToString());
            socket.Options.SetRequestHeader(AgentProtocol.DeviceKeyHeader, secret);
            socket.Options.AddSubProtocol(AgentProtocol.Subprotocol);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            trust.ApplyTo(socket.Options, serverUri.Host);
            var builder = new UriBuilder(new Uri(serverUri, AgentProtocol.RelayPathFor(instrumentCode))) { Scheme = Uri.UriSchemeWss };
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connecting.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(builder.Uri, connecting.Token);
            if (socket.SubProtocol != AgentProtocol.Subprotocol)
            {
                throw new WebSocketException("The server did not agree to the relay protocol.");
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Moves an analyser's bytes between its COM port and the server. The port is read by one loop for as long as it is
/// open (a serial read cannot be cancelled once started); what it reads goes to the relay that is open, or is dropped
/// when none is. The agent never answers the analyser: every byte the analyser receives came from the server.
/// Only byte counts are logged, never the bytes.
/// </summary>
internal sealed class LinePump : IAsyncDisposable
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    private readonly Stream _serial;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _reading;

    // The open relay's socket and the completion source that ends it, swapped together: the port's reader takes both
    // at once, so a failed send on an old relay can only ever end that relay, never the one that replaced it.
    private OpenRelay? _relay;
    private long _toServer;
    private long _toAnalyser;
    private long _dropped;

    public LinePump(Stream serial, ILogger logger)
    {
        _serial = serial;
        _logger = logger;
        _reading = ReadAsync();
    }

    /// <summary>The COM port stopped (closed, unplugged or failed): the line must be opened again.</summary>
    public bool Failed => _reading.IsCompleted;

    public (long ToServer, long ToAnalyser, long Dropped) Traffic => (Interlocked.Read(ref _toServer), Interlocked.Read(ref _toAnalyser), Interlocked.Read(ref _dropped));

    /// <summary>Relays over the socket until it closes, the port stops or <paramref name="cancellationToken"/> fires; returns why it ended.</summary>
    public async Task<string> RelayAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = new OpenRelay(socket, ended);
        Volatile.Write(ref _relay, relay);
        if (Failed)
        {
            ended.TrySetResult("the COM port closed");
        }

        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var downstream = ToAnalyserAsync(socket, ended, receiving.Token);
        string reason;
        try
        {
            reason = await ended.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            reason = "the agent is stopping";
        }

        Interlocked.CompareExchange(ref _relay, null, relay);
        await CloseAsync(socket);
        await receiving.CancelAsync();
        await downstream;
        return reason;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // A serial read ends when its port is closed, which the caller does next.
        }

        _stopping.Dispose();
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await _serial.ReadAsync(buffer, _stopping.Token);
                if (read == 0)
                {
                    break;
                }

                if (Volatile.Read(ref _relay) is not { } relay)
                {
                    Interlocked.Add(ref _dropped, read);
                    continue;
                }

                try
                {
                    using var timeout = new CancellationTokenSource(SendTimeout);
                    await relay.Socket.SendAsync(
                        buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, timeout.Token);
                    Interlocked.Add(ref _toServer, read);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    Interlocked.Add(ref _dropped, read);
                    relay.Ended.TrySetResult("the connection to the server was lost");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The COM port read ended");
        }

        Volatile.Read(ref _relay)?.Ended.TrySetResult("the COM port closed");
    }

    private async Task ToAnalyserAsync(WebSocket socket, TaskCompletionSource<string> ended, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    // The server says why (replaced, revoked, unassigned, settings changed, stopping); it is logged.
                    ended.TrySetResult(ClosedByServer(socket));
                    return;
                }

                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    ended.TrySetResult("the server sent something other than the analyser's bytes");
                    return;
                }

                await _serial.WriteAsync(buffer.AsMemory(0, received.Count), cancellationToken);
                await _serial.FlushAsync(cancellationToken);
                Interlocked.Add(ref _toAnalyser, received.Count);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            ended.TrySetResult("the connection to the server was lost");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Writing to the COM port failed");
            ended.TrySetResult("writing to the COM port failed");
        }
    }

    internal static string ClosedByServer(WebSocket socket)
    {
        var description = socket.CloseStatusDescription;
        var why = string.IsNullOrWhiteSpace(description) ? "no reason given" : description;
        return $"the server closed the relay: {why} ({socket.CloseStatus?.ToString() ?? "no status"})";
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The lab agent closed the relay.", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    private sealed record OpenRelay(WebSocket Socket, TaskCompletionSource<string> Ended);
}
