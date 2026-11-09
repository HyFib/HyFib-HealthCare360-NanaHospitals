using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>How a limit counts in the store all servers share (<see cref="ISharedRateLimitStore"/>).</summary>
public enum SharedLimitKind
{
    /// <summary>
    /// At most <see cref="SharedLimit.Limit"/> requests in any <see cref="SharedLimit.Period"/> (a sliding window): what a
    /// fixed window of the settings (<see cref="FixedWindowLimit"/>) allows, without the burst of twice as many across the
    /// moment one window ends and the next begins.
    /// </summary>
    SlidingWindow,

    /// <summary>
    /// Up to <see cref="SharedLimit.Limit"/> requests at once, <see cref="SharedLimit.Refill"/> more each
    /// <see cref="SharedLimit.Period"/>, as <see cref="TokenBucketLimit"/> says.
    /// </summary>
    TokenBucket,
}

/// <summary>A limit as the shared store counts it; <see cref="Refill"/> is 0 for a sliding window.</summary>
public sealed record SharedLimit(SharedLimitKind Kind, int Limit, int Refill, TimeSpan Period)
{
    public static SharedLimit From(FixedWindowLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return new(SharedLimitKind.SlidingWindow, limit.PermitLimit, 0, limit.Window);
    }

    public static SharedLimit From(TokenBucketLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return new(SharedLimitKind.TokenBucket, limit.TokenLimit, limit.TokensPerPeriod, limit.ReplenishmentPeriod);
    }
}

/// <summary>Whether one more request was allowed, and if not, how long until one would be.</summary>
public readonly record struct SharedLimitDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// Where the request limits other than sign-in and one-time codes (<see cref="RateLimitPolicies"/>) are counted when there
/// are several servers (SCAL-01): a store every server reaches, so a client's budget is the hospital's, not each server's.
/// The server registers one on Redis when <c>ConnectionStrings:Redis</c> is set. Each call counts one request, atomically
/// for every server, measured by the store's own clock, so the servers' clocks need not agree. It throws when the store
/// cannot be reached: the limiter then counts on this server alone (<see cref="SharedRateLimitState"/>).
/// </summary>
public interface ISharedRateLimitStore
{
    Task<SharedLimitDecision> TryTakeAsync(string key, SharedLimit limit, CancellationToken cancellationToken);
}

/// <summary>
/// Whether this server counts its request limits in the shared store or, while that cannot be reached, on its own. A call
/// that fails or takes longer than <see cref="CallTimeout"/> sends every request to this server's own limiter for
/// <see cref="RetryStoreAfter"/>; then the store is tried again. Limits are never lifted meanwhile (fail-safe): each server
/// counts its own, as a single Server Node does, so with <i>n</i> servers a client may make up to <i>n</i> times its
/// budget until the store is back. System health says so (<see cref="SharedRateLimitWarnings"/>).
/// </summary>
public sealed class SharedRateLimitState(TimeProvider time, ILogger<SharedRateLimitState> logger)
{
    /// <summary>How long a request waits for the store before it is counted on this server instead.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>How long requests are counted on this server alone after the store failed, before it is tried again.</summary>
    public static readonly TimeSpan RetryStoreAfter = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private long _skipStoreUntil;
    private DateTimeOffset? _alone;
    private string? _problem;

    /// <summary>Whether a request should be counted in the shared store now.</summary>
    public bool UseStore => time.GetTimestamp() >= Interlocked.Read(ref _skipStoreUntil);

    /// <summary>Since when requests have been counted on this server alone; null while the store counts them.</summary>
    public DateTimeOffset? CountingAloneSince
    {
        get
        {
            lock (_gate)
            {
                return _alone;
            }
        }
    }

    /// <summary>Why the store last failed, for the log and System health.</summary>
    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                return _problem;
            }
        }
    }

    internal void Failed(Exception problem)
    {
        Interlocked.Exchange(ref _skipStoreUntil, time.GetTimestamp() + (long)(RetryStoreAfter.TotalSeconds * time.TimestampFrequency));
        lock (_gate)
        {
            _problem = problem is TimeoutException ? $"it did not answer within {CallTimeout.TotalMilliseconds:0} ms" : problem.Message;
            if (_alone is not null)
            {
                return;
            }

            _alone = time.GetUtcNow();
        }

        logger.LogWarning(problem,
            "The request limits cannot be counted across the servers: until the shared store answers again, each server counts them on its own");
    }

    internal void Succeeded()
    {
        if (CountingAloneSince is null)
        {
            return;
        }

        DateTimeOffset? since;
        lock (_gate)
        {
            since = _alone;
            _alone = null;
            _problem = null;
        }

        if (since is not null)
        {
            logger.LogInformation("The request limits are counted across the servers again, after {Minutes:0.#} minutes on each server alone",
                (time.GetUtcNow() - since.Value).TotalMinutes);
        }
    }
}

/// <summary>System health's warning while this server counts the request limits on its own (<see cref="SharedRateLimitState"/>).</summary>
internal sealed class SharedRateLimitWarnings(SharedRateLimitState state) : IHealthWarningSource
{
    public Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HealthWarning>>(state.CountingAloneSince is { } since
            ? [new("ratelimit.per_server", "warning",
                $"Request limits have been counted on this server alone since {since:yyyy-MM-dd HH:mm} UTC, as Redis cannot be reached ({state.Problem}): " +
                "until it is back, a client may make each limit's requests on every server. Sign-in and one-time code limits are still counted for the whole hospital. " +
                "Is the Redis server up, and does ConnectionStrings:Redis name it?")]
            : []);
}

/// <summary>
/// Makes the limiters of the policies in <see cref="RateLimitPolicies"/> that count in the shared store: one per partition
/// (client address, device credential, user), each keyed in the store by the policy and a keyed hash of the partition, so
/// the store holds no address, credential or user id.
/// </summary>
internal sealed class SharedRateLimits(ISharedRateLimitStore store, SharedRateLimitState state, TimeProvider time)
{
    private const string KeyPurpose = "shared-rate-limit";

    private byte[]? _key;

    /// <summary>The limiters that count in <paramref name="services"/>' shared store; null when there is none (a single Server Node).</summary>
    public static SharedRateLimits? From(IServiceProvider services) =>
        services.GetService<ISharedRateLimitStore>() is { } store
            ? new SharedRateLimits(store, services.GetRequiredService<SharedRateLimitState>(), services.GetService<TimeProvider>() ?? TimeProvider.System)
            : null;

    /// <summary>
    /// A partition counted by <paramref name="limit"/> in the store, and on this server by <paramref name="fallback"/> while the
    /// store cannot be reached.
    /// </summary>
    public RateLimitPartition<string> Partition(HttpContext http, string policy, string partition, SharedLimit limit, Func<ReplenishingRateLimiter> fallback) =>
        RateLimitPartition.Get(partition, key => new SharedRateLimiter(StoreKey(http, policy, key), limit, store, state, fallback(), time));

    internal string StoreKey(HttpContext http, string policy, string partition)
    {
        var key = _key ??= http.RequestServices.GetRequiredService<IFieldProtector>().DeriveKey(KeyPurpose);
        return $"rl:{policy}:" + Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(partition)), 0, 16);
    }
}

/// <summary>
/// One partition's limiter: each request is counted in the shared store, or by <c>fallback</c> on this server while the
/// store cannot be reached. ASP.NET Core's rate-limiting middleware asks every limiter first without waiting
/// (<see cref="RateLimiter.AttemptAcquire"/>) and then, if any refused, waits on each (<see cref="RateLimiter.AcquireAsync"/>);
/// counting in the store means waiting for it, so the first answer is a refusal that counts nothing, and the request is
/// counted once, when it waits.
/// </summary>
internal sealed class SharedRateLimiter(
    string key, SharedLimit limit, ISharedRateLimitStore store, SharedRateLimitState state, ReplenishingRateLimiter fallback, TimeProvider time)
    : ReplenishingRateLimiter
{
    private static readonly RateLimitLease Ask = new Lease(false, null);
    private static readonly RateLimitLease Granted = new Lease(true, null);

    private long _usedAt = time.GetTimestamp();

    /// <summary>The store's key for this partition.</summary>
    public string Key => key;

    /// <summary>
    /// Idle once this server's own count is back to full and no request came for a while: the partition is then dropped,
    /// and made again on the next request, which loses nothing that the store keeps.
    /// </summary>
    public override TimeSpan? IdleDuration =>
        fallback.IdleDuration is { } idle ? TimeSpan.FromTicks(Math.Min(idle.Ticks, time.GetElapsedTime(Interlocked.Read(ref _usedAt)).Ticks)) : null;

    public override RateLimiterStatistics? GetStatistics() => fallback.GetStatistics();

    /// <summary>This server's own count is refilled with the other partitions', by the partitioned limiter's timer.</summary>
    public override bool IsAutoReplenishing => fallback.IsAutoReplenishing;

    public override TimeSpan ReplenishmentPeriod => fallback.ReplenishmentPeriod;

    public override bool TryReplenish() => fallback.TryReplenish();

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        Interlocked.Exchange(ref _usedAt, time.GetTimestamp());
        return permitCount == 0 || !state.UseStore ? fallback.AttemptAcquire(permitCount) : Ask;
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _usedAt, time.GetTimestamp());
        if (permitCount == 0 || !state.UseStore)
        {
            return await fallback.AcquireAsync(permitCount, cancellationToken);
        }

        SharedLimitDecision decision;
        try
        {
            decision = await store.TryTakeAsync(key, limit, cancellationToken).WaitAsync(SharedRateLimitState.CallTimeout, time, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.Failed(ex);
            return await fallback.AcquireAsync(permitCount, cancellationToken);
        }

        state.Succeeded();
        return decision.Allowed ? Granted : new Lease(false, decision.RetryAfter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fallback.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await fallback.DisposeAsync();
        await base.DisposeAsyncCore();
    }

    /// <summary>A lease from the store: nothing to give back, as the store counts the request whatever happens to it.</summary>
    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } wait && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = wait;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
