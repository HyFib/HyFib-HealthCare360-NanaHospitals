using StackExchange.Redis;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// Gives SignalR's Redis backplane (SCAL-02) its connection to Redis, and only once it is connected. The backplane
/// subscribes to the channels the nodes share (messages to every client, group changes and their acknowledgements, client
/// results) when it is given its connection, and never again. Given one while Redis was away, those subscriptions failed
/// for good: once Redis was back, the node missed every message another node sent to all clients, and a group change
/// another node made for one of its clients (telehealth's Admit) waited 30 seconds for an acknowledgement that never came.
/// <para>
/// So while Redis cannot be reached this refuses at once, and the backplane fails that publish or that new connection and
/// asks again next time; nothing waits for Redis. Meanwhile one connection keeps trying in the background
/// (<see cref="ConfigurationOptions.AbortOnConnectFail"/> off), and is handed over as soon as it is connected. After that
/// the connection itself reconnects, and its subscriptions with it, whenever Redis goes away and comes back.
/// </para>
/// </summary>
internal sealed class RedisBackplaneConnections : IAsyncDisposable
{
    private readonly SemaphoreSlim _handing = new(1, 1);
    private ConnectionMultiplexer? _waiting;
    private bool _disposed;

    /// <summary>
    /// The backplane's connection factory: a connection to <paramref name="configuration"/> that is connected now, which
    /// the backplane owns from then on. Throws a <see cref="RedisConnectionException"/> at once while there is none.
    /// </summary>
    public async Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions configuration, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _handing.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = _waiting ?? await ConnectionMultiplexer.ConnectAsync(KeepTrying(configuration), log);
            _waiting = null;
            if (IsReady(connection))
            {
                return connection;
            }

            _waiting = connection;
        }
        finally
        {
            _handing.Release();
        }

        throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "The Redis backplane cannot be reached yet; it is tried again in the background.");
    }

    public async ValueTask DisposeAsync()
    {
        await _handing.WaitAsync();
        try
        {
            _disposed = true;
            if (_waiting is not null)
            {
                await _waiting.DisposeAsync();
                _waiting = null;
            }
        }
        finally
        {
            _handing.Release();
        }

        _handing.Dispose();
    }

    /// <summary>Connected for commands and for subscriptions, which use a connection of their own.</summary>
    private static bool IsReady(ConnectionMultiplexer connection) => connection.IsConnected && connection.GetSubscriber().IsConnected();

    private static ConfigurationOptions KeepTrying(ConfigurationOptions configuration)
    {
        var options = configuration.Clone();
        options.AbortOnConnectFail = false;
        return options;
    }
}
