using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using HyFib.HealthCare360.Server.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SEC-04 and SCAL-01: the audit trail's triggers as every node's start makes them (<see cref="PlatformSql"/>), on a
/// database of its own, upgraded as a hospital's is: partitioned by month (SCAL-04), last year's month among them. An API
/// node may start at any time while the others write to the audit trail. A start whose triggers are as they should be
/// locks nothing of the audit trail; one that finds a trigger dropped, turned off or changed puts it back, waiting at most
/// <see cref="PlatformSql.LockTimeout"/> for the lock it needs, and otherwise stops, saying why.
/// </summary>
public sealed class AuditTriggerTests(HospitalServer server)
{
    /// <summary>A start's statements give up after this long here: long enough to see one hold up an audit write.</summary>
    private const int StartCommandTimeoutSeconds = 10;

    private static readonly DateTime ThisMonth = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime LastYear = ThisMonth.AddYears(-1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LastYearsPartition => $"audit.audit_events_{LastYear:yyyy_MM}";

    private static string ThisMonthsPartition => $"audit.audit_events_{ThisMonth:yyyy_MM}";

    /// <summary>
    /// A node starting while a report holds last year's audit month in an open transaction finishes at once, and an audit
    /// event another node writes meanwhile into this month goes in at once. Start-up dropped and made the audit triggers
    /// again: it locked this month's partition and the newer ones ACCESS EXCLUSIVE, waited for last year's, and every audit
    /// write waited behind it until start-up gave up, a minute later, to try again up to twelve times.
    /// </summary>
    [Fact]
    public async Task A_node_starting_while_a_report_reads_last_years_audit_trail_holds_up_no_audit_write()
    {
        await using var database = await UpgradedDatabaseAsync();
        await using var report = await OpenAsync(database.ConnectionString);
        await using var reading = await report.BeginTransactionAsync(Ct);
        await ExecuteAsync(report, $"SELECT count(*) FROM audit.audit_events WHERE occurred_at >= '{Bound(LastYear)}' AND occurred_at < '{Bound(LastYear.AddMonths(1))}'");

        var starting = Stopwatch.StartNew();
        var start = StartNodeAsync(database.ConnectionString);

        // Once the start is done, or waits for a lock (it waited for last year's month), another node writes an audit event.
        (await SecondNode.EventuallyAsync(async () => start.IsCompleted || await IsWaitingForLockAsync(database.ConnectionString), TimeSpan.FromSeconds(30), Ct))
            .ShouldBeTrue();
        var writing = Stopwatch.StartNew();
        await ExecuteAsync(database.ConnectionString, "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES (now(), 'security', 'it.meanwhile', 'Info')");
        writing.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "an audit event written while a node starts should not wait for the start");

        await start;
        starting.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the start should not wait for the report");
        await reading.RollbackAsync(Ct);
    }

    /// <summary>
    /// A start puts back each audit trigger found dropped, turned off or changed, on the table or on a monthly partition,
    /// as it was. A start that finds them all as they should be locks nothing of the audit trail: it finishes while another
    /// session holds the table and every partition ACCESS EXCLUSIVE.
    /// </summary>
    [Fact]
    public async Task A_start_puts_back_triggers_dropped_turned_off_or_changed_and_otherwise_locks_nothing()
    {
        await using var database = await UpgradedDatabaseAsync();
        var connectionString = database.ConnectionString;
        var asTheyShouldBe = await TriggersAsync(connectionString);
        asTheyShouldBe.ShouldContain($"{LastYearsPartition} trg_protect_audit_events_truncate O");
        asTheyShouldBe.ShouldContain($"{ThisMonthsPartition} trg_check_audit_chain_link O");

        await using (var other = await OpenAsync(connectionString))
        await using (var holding = await other.BeginTransactionAsync(Ct))
        {
            await ExecuteAsync(other, "LOCK TABLE audit.audit_events IN ACCESS EXCLUSIVE MODE");
            var starting = Stopwatch.StartNew();
            await StartNodeAsync(connectionString);
            starting.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "a start with nothing to put back should lock nothing");
            await holding.RollbackAsync(Ct);
        }

        // As someone who owns the database could: the row trigger dropped from the table and so from every month, the
        // chain-link trigger made again to check nothing, the table's TRUNCATE trigger turned off, last year's dropped, and
        // this month's made again to let TRUNCATE through.
        await ExecuteAsync(connectionString, $"""
            CREATE FUNCTION public.it_let_through() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$;
            DROP TRIGGER trg_protect_audit_events ON audit.audit_events;
            DROP TRIGGER trg_check_audit_chain_link ON audit.audit_events;
            CREATE CONSTRAINT TRIGGER trg_check_audit_chain_link AFTER UPDATE ON audit.audit_events DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW WHEN (false) EXECUTE FUNCTION audit.check_audit_chain_link();
            ALTER TABLE ONLY audit.audit_events DISABLE TRIGGER trg_protect_audit_events_truncate;
            DROP TRIGGER trg_protect_audit_events_truncate ON {LastYearsPartition};
            CREATE OR REPLACE TRIGGER trg_protect_audit_events_truncate BEFORE TRUNCATE ON {ThisMonthsPartition}
                FOR EACH STATEMENT EXECUTE FUNCTION public.it_let_through();
            """);
        (await TriggersAsync(connectionString)).ShouldNotBe(asTheyShouldBe);
        await StartNodeAsync(connectionString);
        (await TriggersAsync(connectionString)).ShouldBe(asTheyShouldBe);

        // Turned off alone: one month's copy of the row trigger, and the chain-link trigger on the table and every month
        // (as a replica's, which an ordinary session never fires).
        await ExecuteAsync(connectionString, $"""
            ALTER TABLE {ThisMonthsPartition} DISABLE TRIGGER trg_protect_audit_events;
            ALTER TABLE audit.audit_events ENABLE REPLICA TRIGGER trg_check_audit_chain_link;
            """);
        (await TriggersAsync(connectionString)).ShouldNotBe(asTheyShouldBe);
        await StartNodeAsync(connectionString);
        (await TriggersAsync(connectionString)).ShouldBe(asTheyShouldBe);

        // And they refuse again what they are there to refuse.
        (await RefusalAsync(connectionString, $"DELETE FROM {ThisMonthsPartition}")).ShouldBe("audit events are append-only");
        (await RefusalAsync(connectionString, $"TRUNCATE {LastYearsPartition}")).ShouldBe("audit events are append-only");
        (await RefusalAsync(connectionString, $"TRUNCATE {ThisMonthsPartition}")).ShouldBe("audit events are append-only");
        (await RefusalAsync(connectionString, "TRUNCATE audit.audit_events")).ShouldBe("audit events are append-only");
    }

    /// <summary>
    /// A start that must put a trigger back while something holds the audit trail waits at most
    /// <see cref="PlatformSql.LockTimeout"/>, as does an audit write meanwhile. It changes nothing and stops, saying what to
    /// free, rather than being tried again; once the audit trail is free, the next start puts the trigger back.
    /// </summary>
    [Fact]
    public async Task A_start_that_cannot_lock_the_audit_trail_to_put_a_trigger_back_stops_saying_why()
    {
        await using var database = await UpgradedDatabaseAsync();
        var connectionString = database.ConnectionString;
        var asTheyShouldBe = await TriggersAsync(connectionString);

        // A constraint trigger cannot be replaced: putting it back drops it, which waits for every reader of the trail.
        await ExecuteAsync(connectionString, """
            DROP TRIGGER trg_check_audit_chain_link ON audit.audit_events;
            CREATE CONSTRAINT TRIGGER trg_check_audit_chain_link AFTER UPDATE ON audit.audit_events DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW WHEN (false) EXECUTE FUNCTION audit.check_audit_chain_link();
            """);
        var changed = await TriggersAsync(connectionString);
        await using var report = await OpenAsync(connectionString);
        await using var reading = await report.BeginTransactionAsync(Ct);
        await ExecuteAsync(report, $"SELECT count(*) FROM audit.audit_events WHERE occurred_at >= '{Bound(LastYear)}' AND occurred_at < '{Bound(LastYear.AddMonths(1))}'");

        var starting = Stopwatch.StartNew();
        var start = StartNodeAsync(connectionString);
        (await SecondNode.EventuallyAsync(async () => start.IsCompleted || await IsWaitingForLockAsync(connectionString), TimeSpan.FromSeconds(30), Ct))
            .ShouldBeTrue();
        var writing = Stopwatch.StartNew();
        await ExecuteAsync(connectionString, "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES (now(), 'security', 'it.meanwhile', 'Info')");
        writing.Elapsed.ShouldBeLessThan(PlatformSql.LockTimeout + TimeSpan.FromSeconds(2), "an audit write should wait no longer than the start's lock timeout");

        var stopped = await Should.ThrowAsync<DatabaseUpgradeException>(() => start);
        starting.Elapsed.ShouldBeLessThan(PlatformSql.LockTimeout + TimeSpan.FromSeconds(4));
        stopped.Message.ShouldBe(DatabaseInitializer.AuditInUseMessage);
        ServerComposition.IsTransientDatabaseError(stopped).ShouldBeFalse("start-up should stop, not try again");
        (await TriggersAsync(connectionString)).ShouldBe(changed, "a start that stops changes nothing");

        await reading.RollbackAsync(Ct);
        await StartNodeAsync(connectionString);
        (await TriggersAsync(connectionString)).ShouldBe(asTheyShouldBe);
    }

    /// <summary>
    /// A database as a hospital's is after the upgrade that partitioned the audit trail by month (SCAL-04), started once:
    /// an audit event a year ago and one this month, each in its month's partition, and the triggers made.
    /// </summary>
    private async Task<TestDatabase> UpgradedDatabaseAsync()
    {
        var database = await TestDatabase.CreateAsync(Ct);
        try
        {
            await using (var db = Context(database.ConnectionString, 300))
            {
                var migrations = db.Database.GetMigrations().ToList();
                var partitioning = typeof(PartitionHighVolumeTables).GetCustomAttribute<MigrationAttribute>()!.Id;
                await db.GetService<IMigrator>().MigrateAsync(migrations[migrations.IndexOf(partitioning) - 1], Ct);
                await ExecuteAsync(database.ConnectionString,
                    $"INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES ('{Bound(LastYear.AddDays(3))}', 'security', 'it.last-year', 'Info')");
                await db.Database.MigrateAsync(Ct);
            }

            await StartNodeAsync(database.ConnectionString);
            await ExecuteAsync(database.ConnectionString,
                "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES (now(), 'security', 'it.this-month', 'Info')");
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>What a node's start does to the database (<see cref="DatabaseInitializer.InitializeAsync"/>), without seeders.</summary>
    private async Task StartNodeAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Context(connectionString, StartCommandTimeoutSeconds));
        await using var provider = services.BuildServiceProvider();
        await new DatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformOptions()), NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(Ct);
    }

    /// <summary>Every trigger of the audit trail and its partitions, a line each: where, which, whether on, and what.</summary>
    private static Task<string> TriggersAsync(string connectionString) => ScalarAsync<string>(connectionString, """
        SELECT string_agg(format('%s %s %s %s', t.tgrelid::regclass, t.tgname, t.tgenabled, pg_get_triggerdef(t.oid)), E'\n'
                          ORDER BY t.tgrelid::regclass::text, t.tgname)
        FROM pg_trigger t
        WHERE NOT t.tgisinternal
          AND t.tgrelid IN (SELECT 'audit.audit_events'::regclass UNION ALL SELECT inhrelid FROM pg_inherits WHERE inhparent = 'audit.audit_events'::regclass)
        """);

    /// <summary>Whether a session on the database waits for a table's lock.</summary>
    private static Task<bool> IsWaitingForLockAsync(string connectionString) => ScalarAsync<bool>(connectionString,
        "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE NOT granted AND locktype = 'relation' AND database = (SELECT oid FROM pg_database WHERE datname = current_database()))");

    /// <summary>Why PostgreSQL refused <paramref name="sql"/>, or null when it ran it. Rolled back either way.</summary>
    private static async Task<string?> RefusalAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(Ct);
            return null;
        }
        catch (PostgresException refused)
        {
            return refused.MessageText;
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    /// <summary>A UTC time as a timestamptz literal.</summary>
    private static string Bound(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00";

    private HospitalDbContext Context(string connectionString, int commandTimeout) => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(ServerComposition).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable(MigrationBaseline.HistoryTable, MigrationBaseline.HistorySchema);
                npgsql.CommandTimeout(commandTimeout);
            })
            .UseSnakeCaseNamingConvention()
            .Options,
        server.Services.GetRequiredService<ModuleCatalog>());

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
