using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SCAL-04: the job that keeps the monthly partitions (<see cref="PartitionMaintenance"/>, on the node that runs the
/// jobs) makes the months ahead of the clock and moves rows that waited in the default partition into their new month
/// without changing them (the audit trail too, whose chain still verifies), months gone by included, a month at a time
/// and for a while at most each pass. A table in use, or one that fails, is left to the next pass, and the others are done.
/// It never drops a month, however old: deleting is the Privacy module's retention schedule's alone (legal minimums,
/// medical records reviewed, every run audited). The months used are decades away from the test server's own rows, so
/// nothing other tests wrote is touched; a pass may also move rows other tests left in the default partition, so each test
/// looks at its own months. A test that needs a backlog, or a table to fail, has a database of its own.
/// </summary>
public sealed class PartitionMaintenanceTests(HospitalServer server)
{
    /// <summary>Messages waiting in the default partition for each month of the backlog: some tenths of a second to move.</summary>
    private const int RowsPerMonth = 100_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Months_ahead_are_made_and_rows_waiting_in_the_default_partition_move_into_theirs()
    {
        var month = new DateTime(2041, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var messages = new[] { await MessageAsync(month.AddDays(3)), await MessageAsync(month.AddMonths(1).AddTicks(-10)) };
        var audit = await AuditAsync(month.AddDays(9));

        // Sealed first: the server's sealer, every few seconds, would otherwise seal it while the test looks, and the move
        // must keep a seal anyway.
        await SealAsync(audit);
        (await PartitionOfAsync("platform.notification_logs", messages[0])).ShouldBe("platform.notification_logs_default");
        (await PartitionOfAsync("audit.audit_events", audit)).ShouldBe("audit.audit_events_default");
        var rowsBefore = (await RowTextAsync("platform.notification_logs", messages), await RowTextAsync("audit.audit_events", [audit]));

        var passes = await RunAsync(month.AddDays(20), monthsAhead: 2);

        var made = passes.Single(p => p.Table == "platform.notification_logs");
        made.Created.Where(In2041).ShouldBe(["notification_logs_2041_03", "notification_logs_2041_04", "notification_logs_2041_05"]);
        made.Moved["notification_logs_2041_03"].ShouldBe(2);
        passes.Single(p => p.Table == "audit.audit_events").Moved["audit_events_2041_03"].ShouldBe(1);
        passes.Single(p => p.Table == "virtualcare.rpm_readings").Created.Count(In2041).ShouldBe(3);
        foreach (var message in messages)
        {
            (await PartitionOfAsync("platform.notification_logs", message)).ShouldBe("platform.notification_logs_2041_03");
        }

        (await PartitionOfAsync("audit.audit_events", audit)).ShouldBe("audit.audit_events_2041_03");
        (await RowTextAsync("platform.notification_logs", messages), await RowTextAsync("audit.audit_events", [audit])).ShouldBe(rowsBefore);

        // The moved audit row is still in the chain, and its new month is as append-only as the rest.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AuditChainService>().VerifyAsync(Ct)).Valid.ShouldBeTrue();
        }

        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("TRUNCATE audit.audit_events_2041_03"));
        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM audit.audit_events WHERE id = {audit}"));

        // Nothing more to do on a second pass.
        (await RunAsync(month.AddDays(21), monthsAhead: 2)).ShouldAllBe(p => !p.Created.Any(In2041));
    }

    /// <summary>
    /// Rows the default partition took for a month that went by without a partition of its own (the job did not run for
    /// months, or a device's clock was behind) move into that month's partition at the next pass, unchanged, and the audit
    /// chain still verifies. The job used to make only this month and the months ahead, so they stayed in the default
    /// partition for good.
    /// </summary>
    [Fact]
    public async Task Rows_waiting_in_the_default_partition_for_a_month_gone_by_move_into_that_month()
    {
        var february = new DateTime(2047, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var message = await MessageAsync(february.AddDays(9));
        var call = await IntegrationCallAsync(february.AddMonths(1).AddTicks(-10));
        var audit = await AuditAsync(february.AddDays(1));
        await SealAsync(audit);
        (await PartitionOfAsync("platform.notification_logs", message)).ShouldBe("platform.notification_logs_default");
        (await PartitionOfAsync("integrations.integration_logs", call)).ShouldBe("integrations.integration_logs_default");
        (await PartitionOfAsync("audit.audit_events", audit)).ShouldBe("audit.audit_events_default");
        var rowsBefore = (await RowTextAsync("platform.notification_logs", [message]), await RowTextAsync("integrations.integration_logs", [call]),
            await RowTextAsync("audit.audit_events", [audit]));

        // Seven months on, the job runs again.
        var passes = await RunAsync(february.AddMonths(7).AddDays(14), monthsAhead: 1);

        passes.Single(p => p.Table == "platform.notification_logs").Moved["notification_logs_2047_02"].ShouldBe(1);
        passes.Single(p => p.Table == "integrations.integration_logs").Moved["integration_logs_2047_02"].ShouldBe(1);
        passes.Single(p => p.Table == "audit.audit_events").Moved["audit_events_2047_02"].ShouldBe(1);
        (await PartitionOfAsync("platform.notification_logs", message)).ShouldBe("platform.notification_logs_2047_02");
        (await PartitionOfAsync("integrations.integration_logs", call)).ShouldBe("integrations.integration_logs_2047_02");
        (await PartitionOfAsync("audit.audit_events", audit)).ShouldBe("audit.audit_events_2047_02");
        (await RowTextAsync("platform.notification_logs", [message]), await RowTextAsync("integrations.integration_logs", [call]),
            await RowTextAsync("audit.audit_events", [audit])).ShouldBe(rowsBefore);

        // Only the months with rows waiting: none for the months between that had none.
        passes.Single(p => p.Table == "platform.notification_logs").Created.ShouldNotContain("notification_logs_2047_03");
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AuditChainService>().VerifyAsync(Ct)).Valid.ShouldBeTrue();
        }

        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("TRUNCATE audit.audit_events_2047_02"));
    }

    [Fact]
    public async Task Old_months_are_never_dropped_whatever_their_age()
    {
        var month = new DateTime(2003, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var message = await MessageAsync(month.AddDays(14));
        var call = await IntegrationCallAsync(month.AddDays(14));
        var audit = await AuditAsync(month.AddDays(14));
        await RunAsync(month.AddDays(1), monthsAhead: 1);

        // Twelve years on: a message log entry, an integration call and an audit event from 2003 are all still there, in
        // their month. The message and integration logs are deleted row by row, once the retention the hospital set under
        // Privacy allows, by the privacy retention run.
        await RunAsync(month.AddYears(12), monthsAhead: 1);

        (await PartitionOfAsync("platform.notification_logs", message)).ShouldBe("platform.notification_logs_2003_02");
        (await PartitionOfAsync("integrations.integration_logs", call)).ShouldBe("integrations.integration_logs_2003_02");
        (await PartitionOfAsync("audit.audit_events", audit)).ShouldBe("audit.audit_events_2003_02");
    }

    /// <summary>
    /// A table something holds (a pg_dump, a report with its transaction open) is left to the next pass once its lock is
    /// not granted within <see cref="PartitionMaintenance.LockTimeout"/>, and an insert into it waits at most that long. The
    /// pass used to wait for the lock up to its command timeout (a minute), every insert into the table queued behind it,
    /// with the four tables in one transaction.
    /// </summary>
    [Fact]
    public async Task A_table_in_use_is_left_to_the_next_pass_and_inserts_into_it_wait_only_a_moment()
    {
        var month = new DateTime(2046, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var reader = await OpenAsync();
        await using var reading = await reader.BeginTransactionAsync(Ct);
        await using (var hold = new NpgsqlCommand("LOCK TABLE platform.notification_logs IN ACCESS SHARE MODE", reader, reading))
        {
            await hold.ExecuteNonQueryAsync(Ct);
        }

        var took = Stopwatch.StartNew();
        var running = RunAsync(month.AddDays(3), monthsAhead: 1);

        // A message logged while the pass waits for the message log's lock waits behind the pass.
        (await SecondNode.EventuallyAsync(() => IsLockWaitedForAsync("platform.notification_logs"), TimeSpan.FromSeconds(30), Ct))
            .ShouldBeTrue("the pass should ask for the message log's lock");
        var logging = Stopwatch.StartNew();
        await MessageAsync(DateTime.UtcNow);
        logging.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "a message logged meanwhile should wait no longer than the pass's lock timeout");

        var passes = await running;
        took.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        var messages = passes.Single(p => p.Table == "platform.notification_logs");
        messages.InUse.ShouldBeTrue();
        messages.Created.ShouldBeEmpty();
        passes.Single(p => p.Table == "audit.audit_events").Created.ShouldContain("audit_events_2046_05");
        passes.Single(p => p.Table == "integrations.integration_logs").Created.ShouldContain("integration_logs_2046_05", "the tables after it are done all the same");
        await reading.RollbackAsync(Ct);

        // The next pass makes the message log's months.
        var next = (await RunAsync(month.AddDays(4), monthsAhead: 1)).Single(p => p.Table == "platform.notification_logs");
        next.Created.ShouldContain("notification_logs_2046_05");
        next.Created.ShouldContain("notification_logs_2046_06");
    }

    /// <summary>
    /// Rows waiting in the default partition for several months move a month at a time, each month in a transaction of its
    /// own, so an insert into the table meanwhile waits for one month's rows to move at most. All the months moved in one
    /// transaction, which held the table, and every insert into it, for the whole backlog: 37 s for six months of a million
    /// messages each.
    /// </summary>
    [Fact]
    public async Task Months_waiting_in_the_default_partition_move_one_at_a_time_and_an_insert_waits_for_one_at_most()
    {
        await using var database = await MigratedDatabaseAsync();
        var thisMonth = PartitionMaintenance.MonthOf(DateTimeOffset.UtcNow);
        var months = Enumerable.Range(3, 6).Select(back => thisMonth.AddMonths(-back)).ToList();
        foreach (var month in months)
        {
            await ExecuteAsync(database.ConnectionString, $"""
                INSERT INTO platform.notification_logs (id, created_at, channel, template_key, status)
                SELECT gen_random_uuid(), '{PartitionMaintenance.Bound(month)}'::timestamptz + random() * interval '27 days', 'Sms', 'it.backlog', 'Sent'
                FROM generate_series(1, {RowsPerMonth})
                """);
        }

        await ExecuteAsync(database.ConnectionString, "ANALYZE platform.notification_logs");

        // Messages logged all the while, as the API nodes log them.
        const string Log =
            "INSERT INTO platform.notification_logs (id, created_at, channel, template_key, status) VALUES (gen_random_uuid(), now(), 'Sms', 'it.meanwhile', 'Queued')";
        using var stop = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var logging = Task.Run(async () =>
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            while (!stop.IsCancellationRequested)
            {
                var one = Stopwatch.StartNew();
                await using (var insert = new NpgsqlCommand(Log, connection) { CommandTimeout = 300 })
                {
                    await insert.ExecuteNonQueryAsync(Ct);
                }

                waits.Add(one.Elapsed);
                await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
            }
        }, Ct);

        var took = Stopwatch.StartNew();
        var pass = (await RunAsync(database.ConnectionString, [PartitionedTables.NotificationLogs], DateTime.UtcNow, monthsAhead: 1)).ShouldHaveSingleItem();
        took.Stop();
        await stop.CancelAsync();
        await logging;
        TestContext.Current.SendDiagnosticMessage(
            $"{months.Count} months of {RowsPerMonth} messages moved in {took.Elapsed.TotalSeconds:0.00} s; {waits.Count} messages logged meanwhile, the slowest in {waits.Max().TotalSeconds:0.00} s");

        pass.Moved.Keys.Order().ShouldBe(months.Select(m => $"notification_logs_{m:yyyy_MM}").Order());
        pass.RowsMovedFromDefault.ShouldBe(months.Count * RowsPerMonth);
        (await ScalarAsync<long>(database.ConnectionString, "SELECT count(*) FROM ONLY platform.notification_logs_default")).ShouldBe(0);
        waits.Max().ShouldBeLessThan(took.Elapsed / 2, $"an insert should wait for one month's rows to move at most, not for all {months.Count} (the pass took {took.Elapsed})");
        waits.Count.ShouldBeGreaterThan(5, "messages should have been logged while the pass ran");
    }

    /// <summary>
    /// A pass spends <see cref="PartitionMaintenance.TableBudget"/> at most on a table: the months it has not reached by then
    /// wait for the next pass, oldest first. Each pass makes at least one, so a month that alone takes longer does not hold
    /// up the rest for good.
    /// </summary>
    [Fact]
    public async Task A_pass_leaves_the_months_its_time_for_a_table_did_not_reach_to_the_next_pass()
    {
        await using var database = await MigratedDatabaseAsync();
        var now = new DateTime(2048, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        foreach (var at in new[] { new DateTime(2048, 1, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2048, 3, 5, 0, 0, 0, DateTimeKind.Utc) })
        {
            await ExecuteAsync(database.ConnectionString,
                $"INSERT INTO platform.notification_logs (id, created_at, channel, template_key, status) VALUES ('{Guid.CreateVersion7()}', '{At(at)}', 'Sms', 'it.partition', 'Sent')");
        }

        // No time at all: one month a pass, the months gone by first, then this month and the next.
        string[] months = ["notification_logs_2048_01", "notification_logs_2048_03", "notification_logs_2048_06", "notification_logs_2048_07"];
        for (var i = 0; i < months.Length; i++)
        {
            var pass = (await RunAsync(database.ConnectionString, [PartitionedTables.NotificationLogs], now, monthsAhead: 1, budget: TimeSpan.Zero))
                .ShouldHaveSingleItem();
            pass.Created.ShouldBe([months[i]], $"pass {i + 1}");
            pass.Unfinished.ShouldBe(i < months.Length - 1, $"pass {i + 1}: the months after it should wait for the next pass");
        }

        (await ScalarAsync<long>(database.ConnectionString, "SELECT count(*) FROM ONLY platform.notification_logs_default")).ShouldBe(0);
    }

    /// <summary>
    /// A table whose upkeep fails for another reason than its lock (here moving an audit event out of the default partition
    /// fails) is logged and left to the next pass, and the tables after it are done all the same. Only a lock timeout was
    /// caught: anything else ended the pass, so the tables after the one that failed went without their months for as long
    /// as it failed, and the audit trail comes first.
    /// <para>
    /// The failure is an error, which neither waits for a lock nor races a timer. It was a wait for the lock past a command
    /// timeout of one second, a second before the lock timeout, so the test passed only when Npgsql's cancel reached
    /// PostgreSQL within that second: on a busy machine it sometimes did not, and the pass found the table in use instead.
    /// Every statement of the pass also had that second, the other tables' too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_table_that_fails_is_left_to_the_next_pass_and_the_tables_after_it_are_done()
    {
        await using var database = await MigratedDatabaseAsync();
        var now = new DateTime(2052, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var tables = server.Services.GetServices<PartitionedTable>().ToList();
        tables[0].Name.ShouldBe(PartitionedTables.AuditEvents, "the audit trail comes first");

        // An audit event of this month waits in the default partition, and a trigger there refuses to let it go: moving it
        // into its month fails at once.
        await ExecuteAsync(database.ConnectionString, $"""
            INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES ('{At(now.AddDays(-15))}', 'security', 'it.partition', 'Info');
            CREATE FUNCTION public.it_refuse_move() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'it: this row stays here'; END $$;
            CREATE TRIGGER it_refuse_move BEFORE DELETE ON audit.audit_events_default FOR EACH ROW EXECUTE FUNCTION public.it_refuse_move();
            """);

        var passes = await RunAsync(database.ConnectionString, tables, now, monthsAhead: 1);

        passes.Select(p => p.Table).ShouldBe(tables.Select(t => t.Name), "every table should have its turn");
        var audit = passes[0];
        (audit.Failed, audit.InUse).ShouldBe((true, false));
        audit.Created.ShouldBe(["audit_events_2052_09"], "the month ahead, with no rows to move, is made");
        foreach (var other in passes.Skip(1))
        {
            var table = other.Table.Split('.')[1];
            other.Failed.ShouldBeFalse(other.Table);
            other.Created.ShouldBe([$"{table}_2052_08", $"{table}_2052_09"], other.Table);
        }

        // Once the event may move, the next pass makes the audit trail's month.
        await ExecuteAsync(database.ConnectionString, "DROP TRIGGER it_refuse_move ON audit.audit_events_default");
        var next = (await RunAsync(database.ConnectionString, tables, now, monthsAhead: 1))[0];
        next.Created.ShouldBe(["audit_events_2052_08"]);
        next.Moved["audit_events_2052_08"].ShouldBe(1);
    }

    /// <summary>
    /// An update EF writes for a row of a partitioned table names the row's month, so PostgreSQL reads that partition alone.
    /// By the id alone it looks in every month's: with ten years of months, the audit sealer took 2 seconds to seal 500
    /// events instead of 0.1, and every message sent paid the same per update.
    /// </summary>
    [Fact]
    public async Task An_update_of_a_row_in_a_partitioned_table_reads_only_its_month()
    {
        // Every partitioned table's key in the model is the database's: the id and the column it is partitioned by.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var model = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Model;
            var tables = scope.ServiceProvider.GetServices<PartitionedTable>().ToList();
            tables.ShouldNotBeEmpty();
            foreach (var table in tables)
            {
                var entity = model.GetEntityTypes().Single(e => e.GetTableName() == table.Table && e.GetSchema() == table.Schema);
                entity.FindPrimaryKey().ShouldNotBeNull().Properties.Select(p => p.GetColumnName()).ShouldBe(["id", table.KeyColumn], table.Name);
            }
        }

        // And the statement EF sends to change a message log entry reads its month's partition alone.
        var month = new DateTime(2044, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var message = await MessageAsync(month.AddDays(5));
        await RunAsync(month.AddDays(1), monthsAhead: 1);
        var recorder = new CommandRecorder();
        await using (var db = new HospitalDbContext(
            new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql(ConnectionString()).UseSnakeCaseNamingConvention().AddInterceptors(recorder).Options,
            server.Services.GetRequiredService<ModuleCatalog>()))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            (await db.Set<NotificationLog>().IgnoreQueryFilters().SingleAsync(l => l.Id == message, Ct)).Error = "it.partition";
            await db.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }

        var update = recorder.Commands.Where(c => c.Text.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal)).ShouldHaveSingleItem();
        await using var connection = await OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + update.Text, connection);
        explain.Parameters.AddRange(update.Parameters);
        using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync(Ct))!);
        RelationsIn(plan.RootElement).Where(r => r != "notification_logs").Distinct().ShouldBe(["notification_logs_2044_07"], "the partitions it reads");
    }

    private async Task<IReadOnlyList<PartitionPass>> RunAsync(DateTime now, int monthsAhead)
    {
        var options = new PlatformOptions { Partitions = { MonthsAhead = monthsAhead } };
        await using var scope = server.Services.CreateAsyncScope();
        var maintenance = new PartitionMaintenance(
            scope.ServiceProvider.GetRequiredService<HospitalDbContext>(),
            scope.ServiceProvider.GetServices<PartitionedTable>(),
            Options.Create(options),
            new FixedClock(new DateTimeOffset(now)),
            NullLogger<PartitionMaintenance>.Instance);
        return (await maintenance.RunAsync(Ct)).ShouldNotBeNull("no other node is maintaining the partitions");
    }

    /// <summary>A pass over <paramref name="tables"/> of the database at <paramref name="connectionString"/>, at <paramref name="now"/>.</summary>
    private async Task<IReadOnlyList<PartitionPass>> RunAsync(
        string connectionString, IEnumerable<PartitionedTable> tables, DateTime now, int monthsAhead, TimeSpan? budget = null)
    {
        await using var db = Context(connectionString, 60);
        var maintenance = new PartitionMaintenance(
            db,
            tables,
            Options.Create(new PlatformOptions { Partitions = { MonthsAhead = monthsAhead } }),
            new FixedClock(new DateTimeOffset(now)),
            NullLogger<PartitionMaintenance>.Instance)
        {
            TableBudget = budget ?? PartitionMaintenance.DefaultTableBudget,
        };
        return (await maintenance.RunAsync(Ct)).ShouldNotBeNull("no other node is maintaining the partitions");
    }

    /// <summary>A database of its own, migrated as a new hospital's is: partitions for this month and three ahead.</summary>
    private async Task<TestDatabase> MigratedDatabaseAsync()
    {
        var database = await TestDatabase.CreateAsync(Ct);
        try
        {
            await using var db = Context(database.ConnectionString, 300);
            await db.Database.MigrateAsync(Ct);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

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

    private static bool In2041(string partition) => partition.Contains("_2041_", StringComparison.Ordinal);

    /// <summary>Whether a session waits for an ACCESS EXCLUSIVE lock on <paramref name="table"/>.</summary>
    private Task<bool> IsLockWaitedForAsync(string table) => ScalarAsync<bool>(
        $"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE NOT granted AND mode = 'AccessExclusiveLock' AND relation = '{table}'::regclass)");

    /// <summary>Seals the audit chain up to <paramref name="audit"/>, here or by the server's own sealer.</summary>
    private async Task SealAsync(long audit) =>
        (await SecondNode.EventuallyAsync(async () =>
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AuditChainService>().SealPendingAsync(Ct);
            }

            return await ScalarAsync<bool>($"SELECT hash IS NOT NULL FROM audit.audit_events WHERE id = {audit}");
        }, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue("the audit event should be sealed");

    private static string At(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "+00";

    private async Task<Guid> MessageAsync(DateTime at)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync($"INSERT INTO platform.notification_logs (id, created_at, channel, template_key, status) VALUES ('{id}', '{At(at)}', 'Sms', 'it.partition', 'Sent')");
        return id;
    }

    private async Task<Guid> IntegrationCallAsync(DateTime at)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync($"""
            INSERT INTO integrations.integration_logs (id, created_at, integration, provider, operation, direction, success)
            VALUES ('{id}', '{At(at)}', 'it', 'test', 'partition', 'Outbound', true)
            """);
        return id;
    }

    private async Task<long> AuditAsync(DateTime at) => await ScalarAsync<long>(
        $"INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES ('{At(at)}', 'security', 'it.partition', 'Info') RETURNING id");

    private Task<string> PartitionOfAsync(string table, object id) =>
        ScalarAsync<string>($"SELECT tableoid::regclass::text FROM {table} WHERE id = '{id}'");

    private Task<string> RowTextAsync<T>(string table, IEnumerable<T> ids) =>
        ScalarAsync<string>($"SELECT string_agg(t::text, '|' ORDER BY t.id) FROM {table} t WHERE id IN ({string.Join(", ", ids.Select(i => $"'{i}'"))})");

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(Ct);
    }

    private string ConnectionString() => server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")!;

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>The tables a plan reads, at any depth.</summary>
    private static IEnumerable<string> RelationsIn(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p =>
            p is { Name: "Relation Name", Value.ValueKind: JsonValueKind.String } ? [p.Value.GetString()!] : RelationsIn(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(RelationsIn),
        _ => [],
    };

    /// <summary>The statements EF sends, with their parameters.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<(string Text, NpgsqlParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command) =>
            Commands.Add((command.CommandText, [.. command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone())]));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
