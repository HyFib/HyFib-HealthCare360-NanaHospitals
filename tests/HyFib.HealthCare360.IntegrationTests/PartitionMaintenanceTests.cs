using System.Globalization;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital"));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
