using System.Diagnostics.CodeAnalysis;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Short-lived state every HC360 server on the database shares (SCAL-01), for what must hold whichever server answers:
/// a one-time token or challenge that may be spent only once, and a count of attempts in a window (a per-address limit,
/// failures before a lockout). A server's memory would hold it only for the requests that server answers, so behind a
/// reverse proxy every limit would be multiplied by the number of servers and a spent challenge could be spent again
/// on another one. Each call is one atomic statement on the database every server already uses, committed on its own,
/// whatever transaction the caller has open: a request that fails afterwards does not take its attempt back.
/// </summary>
/// <remarks>
/// Keys are the caller's own, prefixed with what they are for (<c>signin:</c>, <c>portal-challenge:</c>), at most
/// <see cref="ClusterStore.MaxKeyLength"/> characters. Use a hash in a key instead of a personal identifier such as a
/// phone number or an address. Lifetimes and windows are measured by the servers' clocks (<see cref="TimeProvider"/>),
/// which must agree within a second or two, as they must for sign-in tokens. Entries past their end are ignored at once
/// and removed by the node that runs the jobs.
/// </remarks>
public interface IClusterStore
{
    /// <summary>
    /// Adds <paramref name="key"/> for <paramref name="lifetime"/> unless it is there already and has not ended. True for
    /// the one caller, on any server, that added it; false for every other caller until it ends. For spending a one-time
    /// token or challenge: spend it only when this answers true.
    /// </summary>
    Task<bool> TryAddAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts one more for <paramref name="key"/> and returns the count. A key with no count, or whose window ended, starts
    /// again at 1 with a new window of <paramref name="window"/> from now (a fixed window, as the ASP.NET Core one is).
    /// Calls from every server at the same moment are each counted once.
    /// </summary>
    Task<ClusterCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>The count of <paramref name="key"/> in its window; 0 when it has none or the window ended.</summary>
    Task<long> GetCountAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes <paramref name="key"/>: a count starts again from nothing, a token may be added again.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>A count in its window, and when the window ends (for a <c>Retry-After</c>).</summary>
public sealed record ClusterCount(long Count, DateTimeOffset WindowEndsAt);

/// <summary>One key of <see cref="IClusterStore"/>: <c>platform.cluster_entries</c>.</summary>
[NotAudited]
public sealed class ClusterEntry
{
    public string Key { get; set; } = string.Empty;

    /// <summary>The count in the window; 1 for a key added with <see cref="IClusterStore.TryAddAsync"/>.</summary>
    public long Count { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class ClusterEntryConfiguration : IEntityTypeConfiguration<ClusterEntry>
{
    public void Configure(EntityTypeBuilder<ClusterEntry> b)
    {
        b.ToTable("cluster_entries", "platform");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(ClusterStore.MaxKeyLength);
        b.HasIndex(x => x.ExpiresAt);
    }
}

/// <summary>
/// <see cref="IClusterStore"/> on <c>platform.cluster_entries</c>, each call one <c>INSERT … ON CONFLICT</c> or
/// <c>DELETE</c> on a connection of its own (a scope of its own), so it may be used from a singleton and never joins the
/// caller's transaction.
/// </summary>
internal sealed class ClusterStore(IServiceScopeFactory scopes, TimeProvider time) : IClusterStore
{
    public const int MaxKeyLength = 200;

    public async Task<bool> TryAddAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        Check(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        var now = time.GetUtcNow();

        // An entry that ended is taken over as if it were not there; one that has not ended is left alone, and then the
        // statement returns no row.
        const string sql = """
            INSERT INTO platform.cluster_entries AS e (key, count, expires_at) VALUES (@key, 1, @expires)
            ON CONFLICT (key) DO UPDATE SET count = 1, expires_at = EXCLUDED.expires_at WHERE e.expires_at <= @now
            RETURNING 1
            """;
        return await ExecuteAsync(sql, key, now, now + lifetime, async reader => await reader.ReadAsync(cancellationToken), cancellationToken);
    }

    public async Task<ClusterCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        Check(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        var now = time.GetUtcNow();

        // The row is locked from the conflict to the end of the statement, so increments from every server are serialised.
        const string sql = """
            INSERT INTO platform.cluster_entries AS e (key, count, expires_at) VALUES (@key, 1, @expires)
            ON CONFLICT (key) DO UPDATE SET
                count = CASE WHEN e.expires_at <= @now THEN 1 ELSE e.count + 1 END,
                expires_at = CASE WHEN e.expires_at <= @now THEN EXCLUDED.expires_at ELSE e.expires_at END
            RETURNING count, expires_at
            """;
        return await ExecuteAsync(sql, key, now, now + window, async reader =>
        {
            await reader.ReadAsync(cancellationToken);
            return new ClusterCount(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1));
        }, cancellationToken);
    }

    public async Task<long> GetCountAsync(string key, CancellationToken cancellationToken = default)
    {
        Check(key);
        const string sql = "SELECT count FROM platform.cluster_entries WHERE key = @key AND expires_at > @now";
        return await ExecuteAsync(sql, key, time.GetUtcNow(), expires: null,
            async reader => await reader.ReadAsync(cancellationToken) ? reader.GetInt64(0) : 0, cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Check(key);
        await ExecuteAsync("DELETE FROM platform.cluster_entries WHERE key = @key", key, now: null, expires: null,
            _ => Task.FromResult(true), cancellationToken);
    }

    private static void Check(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, MaxKeyLength, nameof(key));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Constant statements; the key and times are parameters.")]
    private async Task<T> ExecuteAsync<T>(
        string sql, string key, DateTimeOffset? now, DateTimeOffset? expires, Func<NpgsqlDataReader, Task<T>> read, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
            command.Parameters.AddWithValue("key", key);
            if (now is { } at)
            {
                command.Parameters.AddWithValue("now", at);
            }

            if (expires is { } end)
            {
                command.Parameters.AddWithValue("expires", end);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await read(reader);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

/// <summary>Removes the entries of <see cref="IClusterStore"/> that ended, every ten minutes, on nodes that run jobs (SCAL-05).</summary>
internal sealed class ClusterStoreCleanup(IServiceScopeFactory scopes, TimeProvider time, IOptions<PlatformOptions> options, ILogger<ClusterStoreCleanup> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var now = time.GetUtcNow();
                var removed = await db.Set<ClusterEntry>().Where(e => e.ExpiresAt <= now).ExecuteDeleteAsync(stoppingToken);
                if (removed > 0)
                {
                    logger.LogDebug("Removed {Count} ended cluster store entries", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Removing the ended cluster store entries failed");
            }
        }
    }

    private static async ValueTask<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
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
}
