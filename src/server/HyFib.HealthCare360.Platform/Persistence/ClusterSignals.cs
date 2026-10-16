using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// A cache of something another server sharing the database can change (SCAL-01): permissions, settings. A change made on
/// one server expires the entry on every server at once (<see cref="IClusterSignals"/>).
/// </summary>
public interface IClusterCache
{
    /// <summary>The kind of change it listens for, such as <c>permissions</c>.</summary>
    string Topic { get; }

    /// <summary>Forgets what is cached for <paramref name="argument"/>, or everything when it is null.</summary>
    void Expire(string? argument);
}

/// <summary>
/// Tells every HC360 server on the database that something it may hold in memory changed, so several API nodes behind a
/// reverse proxy, and the node that runs the jobs, never act on a permission taken away or a setting changed on another
/// node (SCAL-01). It uses PostgreSQL's LISTEN and NOTIFY on the database every node already shares: nothing else to run.
/// </summary>
public interface IClusterSignals
{
    /// <summary>
    /// True while this server hears the signals, its own included. While it does not (the database is away, or a pooler
    /// that does not pass notifications sits in between), caches that must not be stale are not used: every read goes to
    /// the database.
    /// </summary>
    bool IsListening { get; }

    /// <summary>Tells every server, this one included, that what <paramref name="topic"/> caches for <paramref name="argument"/> changed.</summary>
    void Publish(string topic, string? argument = null);
}

/// <summary>
/// <see cref="IClusterSignals"/> over the PostgreSQL channel <see cref="Channel"/>, with one connection that listens and a
/// pooled one that sends. The server sends itself a signal every <see cref="PingInterval"/>; when it has heard none of its
/// own for <see cref="DeafAfter"/>, it counts as not listening. Every time it starts hearing again, every cache forgets
/// what it held: a change may have been signalled meanwhile.
/// <para>
/// The connection is <c>ConnectionStrings:Signals</c> when set, else the hospital connection. Point it at PostgreSQL
/// itself, not at PgBouncer in transaction pooling, which does not pass notifications on (the pings then never come
/// back, the server says so in its log, and caches are bypassed).
/// </para>
/// </summary>
internal sealed class ClusterSignals : BackgroundService, IClusterSignals
{
    public const string Channel = "hc360_cache";
    public const string ConnectionName = "Signals";
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DeafAfter = TimeSpan.FromSeconds(45);
    private const string PingTopic = "ping";
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    private readonly string _node = Guid.NewGuid().ToString("N");
    private readonly Channel<string> _outgoing = System.Threading.Channels.Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly IServiceProvider _services;
    private readonly ILogger<ClusterSignals> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private long _heardAt;
    private bool _warned;

    public ClusterSignals(IServiceProvider services, IConfiguration configuration, DatabaseCredentials credentials, ILogger<ClusterSignals> logger)
    {
        _services = services;
        _logger = logger;
        var connectionString = configuration.GetConnectionString(ConnectionName) is { Length: > 0 } own
            ? own
            : configuration.GetConnectionString("Hospital") ?? throw new InvalidOperationException("Connection string 'Hospital' is not configured.");
        var (connection, supplyPassword) = credentials.For(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connection);
        builder.ConnectionStringBuilder.KeepAlive = 30;
        builder.ConnectionStringBuilder.TcpKeepAlive = true;
        builder.ConnectionStringBuilder.ApplicationName ??= "HC360 cache signals";
        supplyPassword(builder);
        _dataSource = builder.Build();
    }

    public bool IsListening
    {
        get
        {
            var heard = Interlocked.Read(ref _heardAt);
            return heard != 0 && Environment.TickCount64 - heard < (long)DeafAfter.TotalMilliseconds;
        }
    }

    public void Publish(string topic, string? argument = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        _outgoing.Writer.TryWrite(argument is null ? topic : $"{topic}:{argument}");
    }

    public override void Dispose()
    {
        _dataSource.Dispose();
        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ListenAsync(stoppingToken), SendAsync(stoppingToken), PingAsync(stoppingToken));

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, e) => Receive(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                // Anything signalled while this server was not listening was missed: start again from the database.
                ExpireAll();
                Publish(PingTopic, _node);
                var listeningSince = Environment.TickCount64;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(PingInterval, stoppingToken);

                    // A connection that went quiet, or a pooler that keeps notifications to itself: listen again.
                    if (Environment.TickCount64 - Math.Max(Interlocked.Read(ref _heardAt), listeningSince) > (long)DeafAfter.TotalMilliseconds)
                    {
                        throw new TimeoutException($"No cache signal, not even this server's own, was heard for {DeafAfter.TotalSeconds:0} s.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Said once each time it stops hearing, not on every attempt while it cannot.
                if (Interlocked.Exchange(ref _heardAt, 0) != 0 || !_warned)
                {
                    _warned = true;
                    _logger.LogWarning(ex,
                        "Not listening for cache signals from the other servers; caches are bypassed until it is again. If this goes on, is " +
                        "ConnectionStrings:Signals (or the hospital connection) going through a pooler that does not pass PostgreSQL notifications on?");
                }

                await DelayAsync(RetryAfter, stoppingToken);
            }
        }
    }

    private async Task SendAsync(CancellationToken stoppingToken)
    {
        var batch = new HashSet<string>(StringComparer.Ordinal);
        while (await WaitToReadAsync(stoppingToken))
        {
            while (batch.Count < 500 && _outgoing.Reader.TryRead(out var payload))
            {
                batch.Add(payload);
            }

            try
            {
                await using var command = _dataSource.CreateCommand($"SELECT pg_notify('{Channel}', p) FROM unnest(@payloads) AS p");
                command.Parameters.AddWithValue("payloads", batch.ToArray());
                await command.ExecuteNonQueryAsync(stoppingToken);
                batch.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Kept and sent again: an expired cache entry costs one read, a missed one a stale permission.
                _logger.LogWarning(ex, "Cache signals could not be sent to the other servers; trying again");
                await DelayAsync(RetryAfter, stoppingToken);
            }
        }
    }

    private async Task PingAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PingInterval);
        while (await WaitForTickAsync(timer, stoppingToken))
        {
            Publish(PingTopic, _node);
        }
    }

    private void Receive(string payload)
    {
        var colon = payload.IndexOf(':', StringComparison.Ordinal);
        var topic = colon < 0 ? payload : payload[..colon];
        var argument = colon < 0 ? null : payload[(colon + 1)..];
        if (topic == PingTopic)
        {
            if (argument == _node)
            {
                Interlocked.Exchange(ref _heardAt, Math.Max(1, Environment.TickCount64));
                _warned = false;
            }

            return;
        }

        foreach (var cache in _services.GetServices<IClusterCache>().Where(c => c.Topic == topic))
        {
            cache.Expire(argument);
        }
    }

    private void ExpireAll()
    {
        foreach (var cache in _services.GetServices<IClusterCache>())
        {
            cache.Expire(null);
        }
    }

    private async ValueTask<bool> WaitToReadAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await _outgoing.Reader.WaitToReadAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async ValueTask<bool> WaitForTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
