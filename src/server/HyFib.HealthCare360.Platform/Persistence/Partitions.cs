using System.Data;
using System.Globalization;
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
/// <param name="AppendOnly">
/// The audit trail: each new partition gets its own trigger against TRUNCATE, which a partition does not inherit.
/// </param>
public sealed record PartitionedTable(string Schema, string Table, string KeyColumn, bool AppendOnly = false)
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

    /// <summary>The append-only, hash-chained audit trail, kept at least ten years (SEC-04).</summary>
    public static readonly PartitionedTable Audit = new("audit", "audit_events", "occurred_at", AppendOnly: true);

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
/// <param name="InUse">
/// The table was in use: a month's lock was not granted within <see cref="PartitionMaintenance.LockTimeout"/>, so that month
/// and the ones after it were left to the next pass. The months made before it stay made.
/// </param>
public sealed record PartitionPass(string Table, IReadOnlyList<string> Created, int RowsMovedFromDefault, bool InUse = false)
{
    /// <summary>How many rows each partition it made took over from the default partition, for each that took any.</summary>
    public IReadOnlyDictionary<string, int> Moved { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// Something failed for another reason than the table's lock (a statement that ran out of time, an error): it was
    /// logged, what it was doing was left to the next pass, and the pass went on, with this table's other months too.
    /// </summary>
    public bool Failed { get; init; }

    /// <summary>
    /// The pass had spent <see cref="PartitionMaintenance.TableBudget"/> on the table, so the months it had not reached
    /// were left to the next pass.
    /// </summary>
    public bool Unfinished { get; init; }
}

/// <summary>
/// Keeps the monthly partitions ahead of time (SCAL-04): each partitioned table has a partition for this month and the
/// next <see cref="PartitionOptions.MonthsAhead"/>. A month whose rows already went to the default partition gets them
/// moved into its new partition, in the same transaction: a month ahead whose rows came early (a clock far ahead), and a
/// month gone by without a partition (the job not running for months, a clock behind), as far back as
/// <see cref="MonthsBack"/>, as the migration that partitioned the tables did. Audit partitions get the append-only
/// trigger against TRUNCATE that a partition does not inherit. One node at a time (an advisory lock).
/// <para>
/// Making a partition locks its table ACCESS EXCLUSIVE until the month is made, its rows moved included, and while a pass
/// waits for that lock every insert into the table (an audit entry, a reading, a message) waits behind it. So each month
/// has a transaction of its own, which waits at most <see cref="LockTimeout"/> for its locks: a table something holds for
/// long (a pg_dump, a report with its transaction open) is left as it is, and the next pass, six hours later and still
/// months ahead, tries again. An insert waits at most for one month's rows to move, not for a whole backlog of months, and
/// a pass gives each table at most <see cref="TableBudget"/>, leaving the months it has not reached to the next pass.
/// </para>
/// <para>
/// Anything else that fails (a statement that runs out of time, an error) is logged and left to the next pass, and the pass
/// goes on with the table's other months and the other tables: one table that fails, every pass, holds up no other.
/// </para>
/// <para>
/// It never drops a partition. Old rows are deleted by the Privacy module's retention schedule alone, which keeps each kind
/// of record for at least its legal minimum (a year for the message and integration logs under the DPDP Rules), sends
/// medical records such as RPM readings for review instead of deleting them (PRV-02), and audits every run; with the
/// tables partitioned by the same time it deletes by, those deletes read only the months concerned.
/// </para>
/// </summary>
public sealed class PartitionMaintenance(
    HospitalDbContext db,
    IEnumerable<PartitionedTable> tables,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<PartitionMaintenance> logger)
{
    internal const long LockKey = 4_247_363;

    /// <summary>
    /// How long a pass waits for each lock on a table before it leaves the table to the next pass: inserts into the table
    /// wait behind it at most that long.
    /// </summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far back a month whose rows wait in the default partition gets a partition: fifteen years, as far as the
    /// migration that partitioned the tables went. Older rows (a device's clock years out) stay in the default partition.
    /// </summary>
    public const int MonthsBack = 15 * 12;

    /// <summary>
    /// How long a pass goes on making one table's months (moving their rows out of the default partition) before it leaves
    /// the rest to the next pass. It makes at least one month of each table, however long that takes.
    /// </summary>
    public static readonly TimeSpan DefaultTableBudget = TimeSpan.FromMinutes(1);

    /// <summary>See <see cref="DefaultTableBudget"/>.</summary>
    internal TimeSpan TableBudget { get; init; } = DefaultTableBudget;

    private bool IsConnected => db.Database.GetDbConnection().State == ConnectionState.Open;

    /// <summary>
    /// One pass over every partitioned table. Null when another node is doing it now. A table that is not partitioned (its
    /// migration not applied) is skipped; one that is in use, or that fails, is left to the next pass
    /// (<see cref="PartitionPass.InUse"/>, <see cref="PartitionPass.Failed"/>), and the pass goes on with the next.
    /// </summary>
    public async Task<IReadOnlyList<PartitionPass>?> RunAsync(CancellationToken ct)
    {
        var known = tables.DistinctBy(t => t.Name).ToList();

        // One connection for the whole pass, which holds the advisory lock between the months' transactions.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
            {
                return null;
            }

            try
            {
                var thisMonth = MonthOf(time.GetUtcNow());
                var passes = new List<PartitionPass>();
                foreach (var table in known)
                {
                    if (await MaintainAsync(table, thisMonth, ct) is { } pass)
                    {
                        passes.Add(pass);
                    }

                    // A connection that broke took the lock with it: another node may be making partitions now.
                    if (!IsConnected)
                    {
                        logger.LogError(
                            "Partition maintenance stopped after {Table}: its connection to the database was lost, and its lock with it. The next pass goes on",
                            table.Name);
                        break;
                    }
                }

                return passes;
            }
            finally
            {
                await UnlockAsync();
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    // A connection that broke took the lock with it.
    private async Task UnlockAsync()
    {
        try
        {
            await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({LockKey}) AS \"Value\"").SingleAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            logger.LogDebug(ex, "The partition maintenance lock was not released; it went with its connection");
        }
    }

    /// <summary>
    /// One table, a month at a time, each in a transaction of its own, for <see cref="TableBudget"/>. Null when the table is
    /// not partitioned. Throws only when cancelled: what fails is logged and left to the next pass.
    /// </summary>
    private async Task<PartitionPass?> MaintainAsync(PartitionedTable table, DateOnly thisMonth, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var created = new List<string>();
        var moved = new Dictionary<string, int>();
        var (inUse, failed, unfinished) = (false, false, false);
        try
        {
            if (!await IsPartitionedAsync(table, ct))
            {
                return null;
            }

            var months = await MonthsToMakeAsync(table, thisMonth, ct);
            for (var i = 0; i < months.Count && !inUse; i++)
            {
                if (i > 0 && time.GetElapsedTime(started) >= TableBudget)
                {
                    unfinished = true;
                    logger.LogInformation(
                        "Partitions of {Table}: this pass's {Seconds} s for the table are spent; {Count} month(s) wait for the next pass",
                        table.Name, TableBudget.TotalSeconds, months.Count - i);
                    break;
                }

                var partition = table.PartitionFor(months[i]);
                try
                {
                    var rows = await MakeAsync(table, months[i], ct);
                    created.Add(partition);
                    if (rows > 0)
                    {
                        moved[partition] = rows;
                    }
                }
                catch (Exception ex) when (DatabaseInitializer.IsLockTimeout(ex))
                {
                    inUse = true;
                    logger.LogWarning(
                        "Partition {Partition} not made: the table is in use, and its lock was not granted within {Seconds} s. The next pass tries again",
                        partition, LockTimeout.TotalSeconds);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed = true;
                    logger.LogError(ex, "Partition {Partition} not made; the next pass tries again", partition);
                    if (!IsConnected)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failed = true;
            logger.LogError(ex, "Partitions of {Table} not made: finding which it needs failed. The next pass tries again", table.Name);
        }

        if (created.Count > 0)
        {
            logger.LogInformation(
                "Partitions of {Table}: made {Created}; {Moved} rows moved out of the default partition",
                table.Name, string.Join(", ", created), moved.Values.Sum());
        }

        return new PartitionPass(table.Name, created, moved.Values.Sum(), inUse) { Moved = moved, Failed = failed, Unfinished = unfinished };
    }

    /// <summary>
    /// The months without a partition to make: those gone by whose rows wait in the default partition, oldest first (each
    /// moved makes the default partition, which every month made after it reads, smaller), then this month and the months
    /// ahead.
    /// </summary>
    private async Task<List<DateOnly>> MonthsToMakeAsync(PartitionedTable table, DateOnly thisMonth, CancellationToken ct)
    {
        var months = new SortedSet<DateOnly>(await PastMonthsWaitingAsync(table, thisMonth, ct));
        var monthsAhead = Math.Clamp(options.Value.Partitions.MonthsAhead, 1, 24);
        for (var month = thisMonth; month <= thisMonth.AddMonths(monthsAhead); month = month.AddMonths(1))
        {
            months.Add(month);
        }

        var toMake = new List<DateOnly>();
        foreach (var month in months)
        {
            if (!await ExistsAsync(table.Schema, table.PartitionFor(month), ct))
            {
                toMake.Add(month);
            }
        }

        return toMake;
    }

    /// <summary>One month's partition, in a transaction of its own. Returns how many rows it took over from the default partition.</summary>
    private async Task<int> MakeAsync(PartitionedTable table, DateOnly month, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"SET LOCAL lock_timeout = '{LockTimeout.TotalMilliseconds:0}ms'"), ct);
            var rows = await CreateAsync(table, month, ct);
            await transaction.CommitAsync(ct);
            return rows;
        }
        catch
        {
            // When the connection broke, the transaction went with it.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                logger.LogDebug(ex, "The transaction making a partition of {Table} was not rolled back; it went with its connection", table.Name);
            }

            throw;
        }
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

    /// <summary>
    /// The months before <paramref name="thisMonth"/>, as far back as <see cref="MonthsBack"/>, that rows in the default
    /// partition belong to: rows that came while their month had no partition.
    /// </summary>
    private async Task<List<DateOnly>> PastMonthsWaitingAsync(PartitionedTable table, DateOnly thisMonth, CancellationToken ct)
    {
        if (!await ExistsAsync(table.Schema, table.DefaultPartition, ct))
        {
            return [];
        }

        var key = $"\"{table.KeyColumn}\"";
        var sql = $"""
            SELECT DISTINCT (date_trunc('month', {key} AT TIME ZONE 'UTC'))::date AS "Value"
            FROM ONLY "{table.Schema}"."{table.DefaultPartition}"
            WHERE {key} >= '{Bound(thisMonth.AddMonths(-MonthsBack))}' AND {key} < '{Bound(thisMonth)}'
            """;
        return await db.Database.SqlQueryRaw<DateOnly>(sql).ToListAsync(ct);
    }

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

        if (table.AppendOnly)
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
