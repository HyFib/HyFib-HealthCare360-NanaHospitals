using System.Globalization;
using HyFib.HealthCare360.Platform.Http;
using StackExchange.Redis;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The request limits every server shares (<see cref="ISharedRateLimitStore"/>), on the Redis server the real-time
/// backplane uses (<c>ConnectionStrings:Redis</c>, SCAL-02). Each request is one Lua script on one key, which Redis runs
/// atomically, so requests from every server at the same moment are each counted once. The scripts read the time from
/// Redis (<c>TIME</c>), never from the server that asks, so servers whose clocks differ count the same window. Keys start
/// with <see cref="RedisBackplane.ChannelPrefix"/> and expire by themselves once their limit has nothing left to count.
/// <para>
/// It has its own connection, which never waits for Redis: while Redis cannot be reached a call fails at once, and the
/// limiter counts on this server alone (<see cref="SharedRateLimitState"/>). The connection keeps trying in the background
/// and is used again as soon as it is back.
/// </para>
/// </summary>
internal sealed class RedisRateLimitStore(IConfiguration configuration) : ISharedRateLimitStore, IAsyncDisposable
{
    /// <summary>
    /// A sliding window (a log of the requests allowed in the last <c>window</c> milliseconds): drops the entries older
    /// than the window, allows the request and records it if fewer than <c>limit</c> remain, and else says how long until
    /// the oldest leaves the window. Refused requests are not recorded, as ASP.NET Core's fixed window counts none.
    /// Returns { allowed (1 or 0), milliseconds to wait }.
    /// </summary>
    internal const string SlidingWindowScript = """
        if redis.replicate_commands then redis.replicate_commands() end
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local limit = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        if redis.call('ZCARD', KEYS[1]) < limit then
          redis.call('ZADD', KEYS[1], now, now .. ':' .. ARGV[3])
          redis.call('PEXPIRE', KEYS[1], window)
          return { 1, 0 }
        end
        local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        return { 0, tonumber(oldest[2]) + window - now }
        """;

    /// <summary>
    /// A token bucket that holds up to <c>capacity</c> and gains <c>refill</c> at the end of each whole <c>period</c>
    /// milliseconds since it last did, as ASP.NET Core's does on its timer; a new bucket is full. Takes one token if there
    /// is one, else says how long until the next refill. The key expires once the bucket would be full again.
    /// Returns { allowed (1 or 0), milliseconds to wait }.
    /// </summary>
    internal const string TokenBucketScript = """
        if redis.replicate_commands then redis.replicate_commands() end
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local capacity = tonumber(ARGV[1])
        local refill = tonumber(ARGV[2])
        local period = tonumber(ARGV[3])
        local state = redis.call('HMGET', KEYS[1], 'tokens', 'at')
        local tokens = tonumber(state[1])
        local at = tonumber(state[2])
        if tokens == nil or at == nil then
          tokens = capacity
          at = now
        end
        local periods = math.floor((now - at) / period)
        if periods > 0 then
          tokens = math.min(capacity, tokens + periods * refill)
          at = at + periods * period
        end
        local allowed = 0
        local wait = 0
        if tokens >= 1 then
          tokens = tokens - 1
          allowed = 1
        else
          wait = at + period - now
        end
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'at', at)
        redis.call('PEXPIRE', KEYS[1], math.ceil((capacity - tokens) / refill) * period + period)
        return { allowed, wait }
        """;

    private readonly SemaphoreSlim _connecting = new(1, 1);
    private ConnectionMultiplexer? _redis;

    public async Task<SharedLimitDecision> TryTakeAsync(string key, SharedLimit limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limit);
        var redis = await ConnectAsync();
        if (!redis.IsConnected)
        {
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis cannot be reached.");
        }

        var period = (long)Math.Ceiling(limit.Period.TotalMilliseconds);
        var result = limit.Kind == SharedLimitKind.TokenBucket
            ? await redis.GetDatabase().ScriptEvaluateAsync(TokenBucketScript, [StoreKey(key)], [limit.Limit, limit.Refill, period])
            : await redis.GetDatabase().ScriptEvaluateAsync(SlidingWindowScript, [StoreKey(key)],
                [limit.Limit, period, Guid.NewGuid().ToString("N")]);
        var values = (long[])result!;
        return new SharedLimitDecision(values[0] == 1, TimeSpan.FromMilliseconds(Math.Max(0, values[1])));
    }

    /// <summary>The Redis key for a limiter's key: HC360's prefix, so a Redis server shared with other software is not confused.</summary>
    internal static RedisKey StoreKey(string key) => string.Create(CultureInfo.InvariantCulture, $"{RedisBackplane.ChannelPrefix}:{key}");

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        _connecting.Dispose();
    }

    // One connection, made once and kept: it connects again in the background after Redis comes back.
    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        if (_redis is { } connected)
        {
            return connected;
        }

        await _connecting.WaitAsync();
        try
        {
            if (_redis is null)
            {
                var options = ConfigurationOptions.Parse(configuration.GetConnectionString(RedisBackplane.ConnectionName)!);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 2_000;
                options.AsyncTimeout = (int)SharedRateLimitState.CallTimeout.TotalMilliseconds;
                options.BacklogPolicy = BacklogPolicy.FailFast;
                options.ClientName ??= "hc360-rate-limits";
                _redis = await ConnectionMultiplexer.ConnectAsync(options);
            }

            return _redis;
        }
        finally
        {
            _connecting.Release();
        }
    }
}
