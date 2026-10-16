using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The Redis server SignalR shares real-time groups through when there are several HC360 servers (SCAL-02,
/// <c>ConnectionStrings:Redis</c>), as seen from this one: <c>/health/ready</c> reports it Degraded, not Unhealthy, when it
/// cannot be reached. The node still answers the API (a reverse proxy keeps sending it requests), but real-time updates
/// reach only the clients connected to the node that raised them until Redis is back.
/// </summary>
internal sealed class RedisBackplane(IConfiguration configuration) : IHealthCheck, IAsyncDisposable
{
    public const string ConnectionName = "Redis";

    /// <summary>Every channel HC360 uses starts with it, so a Redis server shared with other software is not confused.</summary>
    public const string ChannelPrefix = "hc360";

    private readonly SemaphoreSlim _connecting = new(1, 1);
    private ConnectionMultiplexer? _redis;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var redis = await ConnectAsync();
            if (!redis.IsConnected)
            {
                return new HealthCheckResult(context.Registration.FailureStatus,
                    "The Redis backplane cannot be reached: real-time updates reach only the clients of the server that raised them.");
            }

            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis backplane answers in {latency.TotalMilliseconds:0} ms.");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The Redis backplane does not answer.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        _connecting.Dispose();
    }

    // One connection for the checks, made once and kept: it connects again in the background after Redis comes back.
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
                var options = ConfigurationOptions.Parse(configuration.GetConnectionString(ConnectionName)!);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 2_000;
                options.ClientName ??= "hc360-health";
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
