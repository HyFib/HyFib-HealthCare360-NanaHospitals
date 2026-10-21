using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// Tells the screens to refetch what they show (<see cref="Message"/>) once the Redis backplane is back after an outage
/// (SCAL-02). While Redis was away every real-time event was lost: those this node raised, as it could not send them, and
/// those the other nodes raised, as it could not hear them. A screen connected before stays connected throughout (its
/// connection is to this node, not to Redis), so it neither reconnects nor refetches as it does after a reconnect, and
/// would show a stale queue, bed board or critical alert until its own polling, where it has any, caught up.
/// <para>
/// So once one of this node's backplane connections is up again after it failed, or is first handed over after Redis
/// could not be reached (<see cref="RedisBackplaneConnections"/>), this node sends <see cref="Message"/> to every screen
/// connected to it, on every hub, directly, which needs no Redis; and tells the other nodes through Redis to do the same
/// for theirs (<see cref="Channel"/>): the events it could not send were lost to their screens too, and they may have got
/// their own connection back before it did. Requests that come together are sent once. The apps refetch on it as they
/// do after a reconnect. This node's screens are known by a hub filter, for every hub.
/// </para>
/// </summary>
internal sealed class RealtimeResync(IServiceProvider services, TimeProvider time, ILogger<RealtimeResync> logger) : IHubFilter
{
    /// <summary>What the screens are sent: refetch what you show.</summary>
    public const string Message = "resync";

    /// <summary>Where a node tells the others (after the backplane's channel prefix): the message is the node that tells.</summary>
    internal static readonly RedisChannel Channel = RedisChannel.Literal("HyFib.HealthCare360.Realtime:resync");

    /// <summary>Requests closer together than this are sent once: each connection says so as it comes back, and every node.</summary>
    internal static readonly TimeSpan Gather = TimeSpan.FromSeconds(1);

    private readonly string _node = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, Type> _screens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, IHubContext> _hubs = new();
    private int _pending;

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        await next(context);
        _screens[context.Context.ConnectionId] = context.Hub.GetType();
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            await next(context, exception);
        }
        finally
        {
            _screens.TryRemove(context.Context.ConnectionId, out _);
        }
    }

    /// <summary>
    /// Watches a connection the backplane is handed, from then on. <paramref name="afterOutage"/>: Redis could not be
    /// reached when a connection was asked for before, so events raised meanwhile were lost, and the screens refetch now.
    /// </summary>
    public void Watch(IConnectionMultiplexer connection, bool afterOutage)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var watched = new Watched(afterOutage);
        connection.ConnectionFailed += (_, _) => watched.Lose();

        // Each of its two connections, commands and subscriptions, says so as it comes back; it is back once both are.
        connection.ConnectionRestored += (_, _) =>
        {
            if (connection.IsConnected && connection.GetSubscriber().IsConnected() && watched.Regain())
            {
                _ = BackAsync(connection);
            }
        };
        _ = HearAsync(connection);
        if (watched.Regain())
        {
            _ = BackAsync(connection);
        }
    }

    /// <summary>Tells this node's screens, and the other nodes, that this node's connection is back.</summary>
    private async Task BackAsync(IConnectionMultiplexer connection)
    {
        Request();
        try
        {
            await connection.GetSubscriber().PublishAsync(Channel, _node);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Away again: it says so again when it is back.
            logger.LogDebug(ex, "The other servers could not be told that this server's real-time connection is back");
        }
    }

    /// <summary>Hears the other nodes say their connection is back. The subscription is renewed with the connection.</summary>
    private async Task HearAsync(IConnectionMultiplexer connection)
    {
        try
        {
            await connection.GetSubscriber().SubscribeAsync(Channel, (_, node) =>
            {
                if (node != _node)
                {
                    Request();
                }
            });
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Subscribed all the same: the connection subscribes again as it comes back.
            logger.LogDebug(ex, "Could not yet hear the other servers say their real-time connection is back");
        }
    }

    /// <summary>Sends <see cref="Message"/> to this node's screens, once for requests that come together.</summary>
    private void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _ = SendSoonAsync();
        }
    }

    private async Task SendSoonAsync()
    {
        await Task.Delay(Gather, time);

        // A request from here on is sent again: it may be for events this one is too early for.
        Volatile.Write(ref _pending, 0);
        var told = 0;
        foreach (var (connectionId, hub) in _screens.ToArray())
        {
            try
            {
                // Written to the screen's connection on this node directly, not through Redis.
                await Context(hub).Clients.Client(connectionId).SendAsync(Message);
                told++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // It closed meanwhile; a screen that reconnects refetches anyway.
                logger.LogDebug(ex, "A screen could not be told to refetch");
            }
        }

        logger.LogInformation("The real-time connection through Redis is back: told {Count} screen(s) connected to this server to refetch", told);
    }

    private IHubContext Context(Type hub) =>
        _hubs.GetOrAdd(hub, type => (IHubContext)services.GetRequiredService(typeof(IHubContext<>).MakeGenericType(type)));

    /// <summary>Whether a connection the backplane holds was away, and is to be said back once it is.</summary>
    private sealed class Watched(bool lost)
    {
        private int _lost = lost ? 1 : 0;

        public void Lose() => Volatile.Write(ref _lost, 1);

        /// <summary>True once for every time it was lost.</summary>
        public bool Regain() => Interlocked.Exchange(ref _lost, 0) == 1;
    }
}
