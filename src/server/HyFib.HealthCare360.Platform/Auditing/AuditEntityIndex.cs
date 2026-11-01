using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>
/// The index a record's History reads the audit trail by: (entity_type, entity_id, occurred_at DESC). It is not in the
/// model, where a migration would build it in one statement over every month of the trail and hold every audited write
/// (every write) until it finished. It is built here instead, without holding writes: the parent's index first, empty
/// (<c>ON ONLY</c>), then each monthly partition's <c>CONCURRENTLY</c>, attached to the parent's, which is valid once every
/// partition has its own. Partitions made later take it from the parent. A partition's build that failed leaves an
/// invalid index, dropped and built again by the next pass. One node at a time (an advisory lock).
/// </summary>
public sealed class AuditEntityIndex(HospitalDbContext db, ILogger<AuditEntityIndex> logger)
{
    public const string Name = "ix_audit_events_entity";

    internal const long LockKey = 4_247_364;

    private const string Columns = "(entity_type, entity_id, occurred_at DESC)";

    /// <summary>How long one partition's build may take: a month of a busy hospital's trail is millions of rows.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Builds what is missing. True when the index is complete; false when it is not yet (a partition's build failed and is
    /// left to the next pass) or another node is building it now.
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        var timeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(BuildTimeout);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
            {
                return false;
            }

            try
            {
                return await BuildAsync(ct);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
            db.Database.SetCommandTimeout(timeout);
        }
    }

    private async Task<bool> BuildAsync(CancellationToken ct)
    {
        var partitioned = await db.Database.SqlQuery<bool>(
            $"SELECT c.relkind = 'p' AS \"Value\" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'audit' AND c.relname = 'audit_events'")
            .SingleOrDefaultAsync(ct);
        if (!partitioned)
        {
            // Not partitioned (its migration not applied yet): one index, still without holding writes.
            await DropIfInvalidAsync(Name, ct);
            await ExecuteAsync($"CREATE INDEX CONCURRENTLY IF NOT EXISTS \"{Name}\" ON audit.audit_events {Columns}", ct);
            return await IsValidAsync(Name, ct);
        }

        await ExecuteAsync($"CREATE INDEX IF NOT EXISTS \"{Name}\" ON ONLY audit.audit_events {Columns}", ct);
        var parent = $"audit.{Name}";
        var partitions = await db.Database.SqlQuery<string>($"""
            SELECT c.relname AS "Value"
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = 'audit.audit_events'::regclass
              AND NOT EXISTS (
                  SELECT 1 FROM pg_index x
                  JOIN pg_inherits ii ON ii.inhrelid = x.indexrelid
                  WHERE x.indrelid = c.oid AND ii.inhparent = {parent}::regclass)
            ORDER BY c.relname
            """).ToListAsync(ct);

        foreach (var partition in partitions)
        {
            var index = $"{partition}_entity_idx";
            try
            {
                await DropIfInvalidAsync(index, ct);
                await ExecuteAsync($"CREATE INDEX CONCURRENTLY IF NOT EXISTS \"{index}\" ON audit.\"{partition}\" {Columns}", ct);
                await ExecuteAsync($"ALTER INDEX audit.\"{Name}\" ATTACH PARTITION audit.\"{index}\"", ct);
                logger.LogInformation("Audit trail index for record history: {Partition} done", partition);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Audit trail index for record history: {Partition} not built; the next pass tries again", partition);
            }
        }

        return await IsValidAsync(Name, ct);
    }

    private async Task DropIfInvalidAsync(string index, CancellationToken ct)
    {
        var invalid = await db.Database.SqlQuery<bool>($"""
            SELECT NOT x.indisvalid AS "Value"
            FROM pg_index x JOIN pg_class c ON c.oid = x.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'audit' AND c.relname = {index}
            """).SingleOrDefaultAsync(ct);
        if (invalid)
        {
            await ExecuteAsync($"DROP INDEX CONCURRENTLY IF EXISTS audit.\"{index}\"", ct);
        }
    }

    // Identifiers cannot be parameters: these are the index's own name and the partitions' names as PostgreSQL lists them.
    private Task<int> ExecuteAsync(string sql, CancellationToken ct) => db.Database.ExecuteSqlRawAsync(sql, ct);

    private async Task<bool> IsValidAsync(string index, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT x.indisvalid AS "Value"
            FROM pg_index x JOIN pg_class c ON c.oid = x.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'audit' AND c.relname = {index}
            """).SingleOrDefaultAsync(ct);
}

/// <summary>
/// Runs <see cref="AuditEntityIndex"/> two minutes after start and every six hours, on nodes that run jobs, until the
/// index is complete; then only checks it (a partition made since takes it from the parent).
/// </summary>
internal sealed class AuditEntityIndexJob(IServiceScopeFactory scopes, TimeProvider time, IOptions<PlatformOptions> options, ILogger<AuditEntityIndexJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AuditEntityIndex>().EnsureAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The audit trail index for record history could not be built; History reads the trail without it meanwhile");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
