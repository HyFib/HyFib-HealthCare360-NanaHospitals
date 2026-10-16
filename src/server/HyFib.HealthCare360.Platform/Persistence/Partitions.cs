using System.Globalization;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// A table split into one partition per calendar month (UTC) of <see cref="KeyColumn"/>, plus a default partition for rows
/// outside every month that has one (SCAL-04). The migration <c>PartitionHighVolumeTables</c> made it so; modules declare
/// theirs with <see cref="PartitionServiceCollectionExtensions.AddMonthlyPartitions"/>.
/// </summary>
/// <param name="Retainable">
/// Whether <see cref="PartitionOptions.RetentionMonths"/> may drop its old partitions. Never for the audit trail.
/// </param>
public sealed record PartitionedTable(string Schema, string Table, string KeyColumn, bool Retainable = true)
{
    public string Name => $"{Schema}.{Table}";

    public string Qualified => $"\"{Schema}\".\"{Table}\"";

    public string DefaultPartition => $"{Table}_default";

    public string PartitionFor(DateOnly month) => string.Create(CultureInfo.InvariantCulture, $"{Table}_{month.Year:D4}_{month.Month:D2}");
}

/// <summary>The tables the platform itself partitions by month (SCAL-04).</summary>
public static class PartitionedTables
{
    public const string AuditEvents = "audit.audit_events";

    /// <summary>The append-only, hash-chained audit trail, kept at least ten years (SEC-04): never dropped.</summary>
    public static readonly PartitionedTable Audit = new("audit", "audit_events", "occurred_at", Retainable: false);

    /// <summary>Every SMS, WhatsApp message and e-mail sent to patients and staff.</summary>
    public static readonly PartitionedTable NotificationLogs = new("platform", "notification_logs", "created_at");

    /// <summary>Every call to and from an outside system: analyser messages, gateways, webhooks.</summary>
    public static readonly PartitionedTable IntegrationLogs = new("integrations", "integration_logs", "created_at");
}

public static class PartitionServiceCollectionExtensions
{
    /// <summary>Has <see cref="PartitionMaintenance"/> look after a table a migration partitioned by month.</summary>
    public static IServiceCollection AddMonthlyPartitions(this IServiceCollection services, PartitionedTable table)
    {
        services.AddSingleton(table);
        return services;
    }
}

/// <summary>What one maintenance pass did to one table.</summary>
public sealed record PartitionPass(string Table, IReadOnlyList<string> Created, IReadOnlyList<string> Dropped, int RowsMovedFromDefault);

/// <summary>
/// Keeps the monthly partitions ahead of time (SCAL-04): each partitioned table has a partition for this month and the
/// next <see cref="PartitionOptions.MonthsAhead"/>. A month whose rows already went to the default partition (a clock far
/// ahead, or the job not running for months) gets them moved into its new partition, in the same transaction. Only when a
/// retention is set for a table (<see cref="PartitionOptions.RetentionMonths"/>, none by default) are its partitions that
/// lie entirely before it dropped, whole; rows in the default partition are never dropped. Audit partitions get the
/// append-only trigger against TRUNCATE that a partition does not inherit. One node at a time (an advisory lock).
/// </summary>
public sealed partial class PartitionMaintenance(
    HospitalDbContext db,
    IEnumerable<PartitionedTable> tables,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<PartitionMaintenance> logger)
{
    internal const long LockKey = 4_247_363;

    /// <summary>
    /// One pass over every partitioned table. Null when another node is doing it now. A table that is not partitioned (its
    /// migration not applied) is skipped.
    /// </summary>
    public async Task<IReadOnlyList<PartitionPass>?> RunAsync(CancellationToken ct)
    {
        var settings = options.Value.Partitions;
        var known = tables.DistinctBy(t => t.Name).ToList();
        foreach (var unknown in settings.RetentionMonths.Keys.Where(k => !known.Any(t => string.Equals(t.Name, k, StringComparison.OrdinalIgnoreCase))))
        {
            logger.LogWarning("HealthCare360:Partitions:RetentionMonths names {Table}, which is not a partitioned table; it is ignored", unknown);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
        {
            return null;
        }

        var thisMonth = MonthOf(time.GetUtcNow());
        var passes = new List<PartitionPass>();
        foreach (var table in known)
        {
            if (!await IsPartitionedAsync(table, ct))
            {
                continue;
            }

            var created = new List<string>();
            var moved = 0;
            for (var month = thisMonth; month <= thisMonth.AddMonths(Math.Clamp(settings.MonthsAhead, 1, 24)); month = month.AddMonths(1))
            {
                if (await ExistsAsync(table.Schema, table.PartitionFor(month), ct))
                {
                    continue;
                }

                moved += await CreateAsync(table, month, ct);
                created.Add(table.PartitionFor(month));
            }

            var dropped = new List<string>();
            if (table.Retainable && settings.RetentionMonths.TryGetValue(table.Name, out var keep) && keep >= 1)
            {
                dropped.AddRange(await DropOlderThanAsync(table, thisMonth.AddMonths(-keep), ct));
            }

            passes.Add(new PartitionPass(table.Name, created, dropped, moved));
        }

        await transaction.CommitAsync(ct);
        foreach (var pass in passes.Where(p => p.Created.Count + p.Dropped.Count > 0))
        {
            logger.LogInformation(
                "Partitions of {Table}: made {Created}, dropped {Dropped} (older than the retention); {Moved} rows moved out of the default partition",
                pass.Table, pass.Created.Count == 0 ? "none" : string.Join(", ", pass.Created), pass.Dropped.Count == 0 ? "none" : string.Join(", ", pass.Dropped),
                pass.RowsMovedFromDefault);
        }

        return passes;
    }

    /// <summary>The first day of the UTC month <paramref name="at"/> is in.</summary>
    public static DateOnly MonthOf(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1);

    /// <summary>A partition bound: the start of a UTC month, written so the session's time zone cannot move it.</summary>
    public static string Bound(DateOnly month) => month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00+00";

    private async Task<bool> IsPartitionedAsync(PartitionedTable table, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM pg_partitioned_table p
                JOIN pg_class c ON c.oid = p.partrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = {table.Schema} AND c.relname = {table.Table}) AS "Value"
            """).SingleAsync(ct);

    private async Task<bool> ExistsAsync(string schema, string relation, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"SELECT to_regclass({$"\"{schema}\".\"{relation}\""}) IS NOT NULL AS \"Value\"").SingleAsync(ct);

    /// <summary>Makes the month's partition. Returns how many rows it took over from the default partition.</summary>
    private async Task<int> CreateAsync(PartitionedTable table, DateOnly month, CancellationToken ct)
    {
        var partition = $"\"{table.Schema}\".\"{table.PartitionFor(month)}\"";
        var defaultPartition = $"\"{table.Schema}\".\"{table.DefaultPartition}\"";
        var range = $"FOR VALUES FROM ('{Bound(month)}') TO ('{Bound(month.AddMonths(1))}')";
        var inRange = $"\"{table.KeyColumn}\" >= '{Bound(month)}' AND \"{table.KeyColumn}\" < '{Bound(month.AddMonths(1))}'";
        var hasDefault = await ExistsAsync(table.Schema, table.DefaultPartition, ct);
        var waitingSql = $"SELECT EXISTS (SELECT 1 FROM ONLY {defaultPartition} WHERE {inRange}) AS \"Value\"";
        var waiting = hasDefault && await db.Database.SqlQueryRaw<bool>(waitingSql).SingleAsync(ct);

        var moved = 0;
        if (!waiting)
        {
            await ExecuteAsync($"CREATE TABLE {partition} PARTITION OF {table.Qualified} {range}", ct);
        }
        else
        {
            // PostgreSQL will not make a partition for rows the default partition holds. The default partition is taken out
            // for a moment, so its rows move without an UPDATE or DELETE on the partitioned table (the audit trail forbids
            // both): they are copied into the new month's table, removed from the default, and both are attached again.
            await ExecuteAsync($"LOCK TABLE {table.Qualified} IN ACCESS EXCLUSIVE MODE", ct);
            await ExecuteAsync($"ALTER TABLE {table.Qualified} DETACH PARTITION {defaultPartition}", ct);
            await ExecuteAsync($"CREATE TABLE {partition} (LIKE {table.Qualified} INCLUDING DEFAULTS INCLUDING CONSTRAINTS)", ct);
            moved = await ExecuteAsync($"INSERT INTO {partition} SELECT * FROM {defaultPartition} WHERE {inRange}", ct);
            await ExecuteAsync($"DELETE FROM {defaultPartition} WHERE {inRange}", ct);
            await ExecuteAsync($"ALTER TABLE {table.Qualified} ATTACH PARTITION {partition} {range}", ct);
            await ExecuteAsync($"ALTER TABLE {table.Qualified} ATTACH PARTITION {defaultPartition} DEFAULT", ct);
        }

        if (!table.Retainable)
        {
            await ProtectFromTruncateAsync(partition, ct);
        }

        return moved;
    }

    /// <summary>
    /// A partition does not inherit a statement trigger: without its own, TRUNCATE on the partition itself would empty a
    /// month of the append-only audit trail. Made only once the trigger function exists (after the first start).
    /// </summary>
    private async Task ProtectFromTruncateAsync(string partition, CancellationToken ct)
    {
        if (await db.Database.SqlQuery<bool>($"SELECT to_regprocedure('audit.protect_audit_events()') IS NOT NULL AS \"Value\"").SingleAsync(ct))
        {
            await ExecuteAsync(
                $"CREATE TRIGGER trg_protect_audit_events_truncate BEFORE TRUNCATE ON {partition} FOR EACH STATEMENT EXECUTE FUNCTION audit.protect_audit_events()", ct);
        }
    }

    // Partition DDL names tables and bounds, which cannot be parameters. Every name comes from a PartitionedTable a module
    // registered and every bound from a date, never from a request.
    private Task<int> ExecuteAsync(string sql, CancellationToken ct) => db.Database.ExecuteSqlRawAsync(sql, ct);

    /// <summary>Drops the month partitions that end on or before <paramref name="before"/>: nothing in them is newer.</summary>
    private async Task<IReadOnlyList<string>> DropOlderThanAsync(PartitionedTable table, DateOnly before, CancellationToken ct)
    {
        var partitions = await db.Database.SqlQuery<string>($"""
            SELECT c.relname AS "Value"
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            JOIN pg_class p ON p.oid = i.inhparent
            JOIN pg_namespace n ON n.oid = p.relnamespace
            WHERE n.nspname = {table.Schema} AND p.relname = {table.Table}
            """).ToListAsync(ct);

        var dropped = new List<string>();
        foreach (var name in partitions.Order(StringComparer.Ordinal))
        {
            var match = MonthPartition().Match(name);
            if (!match.Success || !name.StartsWith(table.Table + "_", StringComparison.Ordinal) || name.Length != table.Table.Length + 8)
            {
                continue;
            }

            var month = new DateOnly(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 1);
            if (month.AddMonths(1) > before)
            {
                continue;
            }

            await ExecuteAsync($"ALTER TABLE {table.Qualified} DETACH PARTITION \"{table.Schema}\".\"{name}\"", ct);
            await ExecuteAsync($"DROP TABLE \"{table.Schema}\".\"{name}\"", ct);
            dropped.Add(name);
        }

        return dropped;
    }

    [GeneratedRegex(@"_(\d{4})_(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthPartition();
}

/// <summary>
/// Runs <see cref="PartitionMaintenance"/> a minute after start and every six hours, on nodes that run jobs (SCAL-05), so
/// next month's partitions are always there long before the month starts.
/// </summary>
internal sealed class PartitionMaintenanceJob(IServiceScopeFactory scopes, TimeProvider time, IOptions<PlatformOptions> options, ILogger<PartitionMaintenanceJob> logger)
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
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
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
                await scope.ServiceProvider.GetRequiredService<PartitionMaintenance>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Monthly partition maintenance failed; rows for a month without a partition go to the default partition meanwhile");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
