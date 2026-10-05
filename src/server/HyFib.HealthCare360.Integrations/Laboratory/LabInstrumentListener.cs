using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// TCP listener for one analyser. Connections from addresses outside the instrument's allow-list are closed
/// immediately, and at most <c>maxConnections</c> are served at once (analysers normally hold one), so a scanner or a
/// misbehaving device cannot exhaust the gateway.
/// </summary>
internal sealed class LabInstrumentListener : IAsyncDisposable
{
    private readonly LabRuntimeOptions _options;
    private readonly int _maxConnections;
    private readonly ILabMessageProcessor _processor;
    private readonly LabGatewayState _state;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly IReadOnlySet<IPAddress>? _allowed;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _active;
    private int _connectionIds;

    public LabInstrumentListener(
        LabInstrumentSettings instrument, IPAddress bindAddress, LabRuntimeOptions options, int maxConnections,
        ILabMessageProcessor processor, LabGatewayState state, TimeProvider time, ILogger logger)
    {
        Instrument = instrument;
        _options = options;
        _maxConnections = Math.Clamp(maxConnections, 1, 32);
        _processor = processor;
        _state = state;
        _time = time;
        _logger = logger;
        _listener = new TcpListener(bindAddress, instrument.Port);
        _allowed = ParseAllowList(instrument.AllowedIps);
    }

    public LabInstrumentSettings Instrument { get; }

    /// <summary>Binds the port (throws <see cref="SocketException"/> when it is in use) and starts accepting.</summary>
    public void Start()
    {
        _listener.Start(backlog: 8);
        _state.Listening(Instrument.Code, Instrument.Protocol, Instrument.Port);
        _acceptLoop = AcceptLoopAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
            await Task.WhenAll(_connections.Values).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException or SocketException)
        {
            _logger.LogDebug(ex, "Listener for instrument {Instrument} stopped with pending connections", Instrument.Code);
        }

        _stopping.Dispose();
        _state.Stopped(Instrument.Code);
    }

    internal static IReadOnlySet<IPAddress>? ParseAllowList(IReadOnlyList<string>? addresses)
    {
        var parsed = (addresses ?? [])
            .Select(a => IPAddress.TryParse(a.Trim(), out var ip) ? Normalize(ip) : null)
            .OfType<IPAddress>()
            .ToHashSet();
        return parsed.Count == 0 ? null : parsed;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accept failed on instrument {Instrument} port {Port}", Instrument.Code, Instrument.Port);
                continue;
            }

            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address is { } address ? Normalize(address) : null;
            if (_allowed is not null && (remote is null || !_allowed.Contains(remote)))
            {
                _logger.LogWarning("Refused connection from {Address} to instrument {Instrument}: not in its allow-list", remote, Instrument.Code);
                client.Dispose();
                continue;
            }

            if (Interlocked.Increment(ref _active) > _maxConnections)
            {
                Interlocked.Decrement(ref _active);
                _logger.LogWarning("Refused connection from {Address} to instrument {Instrument}: too many open connections", remote, Instrument.Code);
                client.Dispose();
                continue;
            }

            var id = Interlocked.Increment(ref _connectionIds);
            var task = HandleAsync(id, client, cancellationToken);
            _connections[id] = task;
            if (task.IsCompleted)
            {
                _connections.TryRemove(id, out _);
            }
        }
    }

    private async Task HandleAsync(int id, TcpClient client, CancellationToken cancellationToken)
    {
        _state.ConnectionOpened(Instrument.Code);
        try
        {
            using (client)
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                await LabSessionRunner.RunAsync(Instrument, _options, client.GetStream(), _processor, _time, _logger, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Connection to instrument {Instrument} closed", Instrument.Code);
        }
        catch (Exception ex)
        {
            // Anything else is a bug in the session code; one connection must never take the gateway down.
            _logger.LogError(ex, "Unexpected error on a connection from instrument {Instrument}", Instrument.Code);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            _state.ConnectionClosed(Instrument.Code);
            _connections.TryRemove(id, out _);
        }
    }
}
