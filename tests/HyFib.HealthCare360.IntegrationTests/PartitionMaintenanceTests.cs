using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
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
/// without changing them (the audit trail too, whose chain still verifies). It never drops a month, however old: deleting
/// is the Privacy module's retention schedule's alone (legal minimums, medical records reviewed, every run audited). The
/// months used are decades away from the test server's own rows, so nothing other tests wrote is touched.
/// </summary>
public sealed class PartitionMaintenanceTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Months_ahead_are_made_and_rows_waiting_in_the_default_partition_move_into_theirs()
    {
        var month = new DateTime(2041, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var messages = new[] { await MessageAsync(month.AddDays(3)), await MessageAsync(month.AddMonths(1).AddTicks(-10)) };
        var audit = await AuditAsync(month.AddDays(9));
        (await PartitionOfAsync("platform.notification_logs", messages[0])).ShouldBe("platform.notification_logs_default");
        (await PartitionOfAsync("audit.audit_events", audit)).ShouldBe("audit.audit_events_default");
        var rowsBefore = (await RowTextAsync("platform.notification_logs", messages), await RowTextAsync("audit.audit_events", [audit]));

        var passes = await RunAsync(month.AddDays(20), monthsAhead: 2);

        var made = passes.Single(p => p.Table == "platform.notification_logs");
        made.Created.ShouldBe(["notification_logs_2041_03", "notification_logs_2041_04", "notification_logs_2041_05"]);
        made.RowsMovedFromDefault.ShouldBe(2);
        passes.Single(p => p.Table == "audit.audit_events").RowsMovedFromDefault.ShouldBe(1);
        passes.Single(p => p.Table == "virtualcare.rpm_readings").Created.Count.ShouldBe(3);
        foreach (var message in messages)
        {
            (await PartitionOfAsync("platform.notification_logs", message)).ShouldBe("platform.notification_logs_2041_03");
        }

        (await PartitionOfAsync("audit.audit_events", audit)).ShouldBe("audit.audit_events_2041_03");
        (await RowTextAsync("platform.notification_logs", messages), await RowTextAsync("audit.audit_events", [audit])).ShouldBe(rowsBefore);

        // The moved audit row is still in the chain, and its new month is as append-only as the rest.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var chain = scope.ServiceProvider.GetRequiredService<AuditChainService>();
            await chain.SealPendingAsync(Ct);
            (await chain.VerifyAsync(Ct)).Valid.ShouldBeTrue();
        }

        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("TRUNCATE audit.audit_events_2041_03"));
        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM audit.audit_events WHERE id = {audit}"));

        // Nothing more to do on a second pass.
        (await RunAsync(month.AddDays(21), monthsAhead: 2)).ShouldAllBe(p => p.Created.Count == 0);
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
