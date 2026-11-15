using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A restore rewinds every number series with the rest of the database: the UHIDs, bills and receipts issued after the
/// backup was made would be issued again, though they are on cards, wristbands and GST invoices already. The node that runs
/// the jobs keeps a high-water mark of every series outside the database (<see cref="NumberSeriesHighWaterJob"/>), and
/// start-up moves each series the database has behind it past it (<see cref="NumberSeriesStartupCheck"/>), before the
/// server takes requests. The series here are internal ones of the tests' own, so no other test's numbers move.
/// </summary>
public sealed class NumberSeriesRestoreTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Numbers are issued, the backup holds the series' row as it was, more are issued and the mark is written, and the row
    /// is put back as the backup had it (what a restore does to it); a series whose first number came after the backup is
    /// not in it at all. Start-up's check then moves both past every number issued before, and the next start moves nothing.
    /// </summary>
    [Fact]
    public async Task After_a_restore_the_next_number_is_past_every_number_issued_before_it()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var visits = Hidden("ITR", suffix);
        var receipts = Hidden("ITN", suffix);
        var machine = NewMachine(suffix);
        try
        {
            var issued = await IssueAsync(visits, 3);
            var backup = await RowAsync(server.Services, visits.Key);
            issued.AddRange(await IssueAsync(visits, 5));
            var lost = await IssueAsync(receipts, 4);

            var mark = Mark(machine);
            (await RecordAsync(mark)).ShouldBeTrue("the node that runs the jobs writes the mark, every minute while numbers are issued");

            // The restore: the series back as the backup had it (its last change included), the other gone.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                await db.Set<NumberSeries>().IgnoreQueryFilters().Where(s => s.Key == visits.Key).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CurrentValue, backup.CurrentValue)
                    .SetProperty(x => x.CurrentPeriod, backup.CurrentPeriod)
                    .SetProperty(x => x.UpdatedAt, backup.UpdatedAt), Ct);
                await db.Set<NumberSeries>().IgnoreQueryFilters().Where(s => s.Key == receipts.Key).ExecuteDeleteAsync(Ct);
            }

            (await RowAsync(server.Services, visits.Key)).CurrentValue.ShouldBe(3, "the restored series would issue the fourth number again");

            await StartAsync(mark, machine);

            var next = Sequence((await IssueAsync(visits, 1)).ShouldHaveSingleItem());
            next.ShouldBeGreaterThan(issued.Max(Sequence), "past every number issued before the restore, those after the backup included");
            next.ShouldBe(issued.Max(Sequence) + NumberSeriesHighWater.Margin + 1);
            Sequence((await IssueAsync(receipts, 1)).ShouldHaveSingleItem())
                .ShouldBe(lost.Max(Sequence) + NumberSeriesHighWater.Margin + 1, "the series the backup did not have is made again past its numbers");

            await using (var scope = server.Services.CreateAsyncScope())
            {
                var raised = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                    .Where(a => a.Action == NumberSeriesStartupCheck.RaisedAction && (a.EntityId == visits.Key || a.EntityId == receipts.Key))
                    .ToListAsync(Ct);
                raised.Count.ShouldBe(2, "each raise is in the audit trail");
                raised.ShouldAllBe(a => a.Severity == AuditSeverity.Warning);
                raised.Single(a => a.EntityId == visits.Key).Summary.ShouldNotBeNull().ShouldContain($"next ITR{next:000000}, not ITR000004");
            }

            // Started again, nothing more moves: the series are past the mark.
            await StartAsync(mark, machine);
            Sequence((await IssueAsync(visits, 1)).ShouldHaveSingleItem()).ShouldBe(next + 1);
        }
        finally
        {
            Directory.Delete(machine.Root, recursive: true);
        }
    }

    /// <summary>
    /// The whole way, as on new hardware: a backup, numbers issued after it, the backup restored as Restore-HC360.ps1 restores
    /// it, with its marker beside it, and HC360 starting on the restored database. Outgoing messages are held there, the
    /// restore is recorded with the series moved for System health, and the marker is renamed.
    /// </summary>
    [Fact]
    public async Task After_Restore_HC360_start_up_holds_the_messages_and_records_the_numbers_it_moved()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var bills = Hidden("ITB", suffix);
        var machine = NewMachine(suffix);
        string? backup = null;
        try
        {
            await IssueAsync(bills, 2);
            (backup, var recoveryKey) = await BackupAsync();
            var issued = await IssueAsync(bills, 3);
            var mark = Mark(machine);
            (await RecordAsync(mark)).ShouldBeTrue();

            await using var restored = await ScriptedRestore.RunAsync(await DecryptAsync(backup, recoveryKey, machine), Ct);
            var restoredAt = new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
            await File.WriteAllTextAsync(Path.Combine(machine.Root, RestoreMarker.FileName), JsonSerializer.Serialize(
                new { restoredAt, backupFile = backup, backupTakenAt = (DateTimeOffset?)null }, Web), Ct);

            await StartAsync(mark, machine, restored.ConnectionString);

            File.Exists(Path.Combine(machine.Root, RestoreMarker.FileName)).ShouldBeFalse("the marker is acted on once");
            Directory.GetFiles(machine.Root, "restored-*.json").ShouldHaveSingleItem();

            // The hold the outbox reads, in the restored database.
            using (var hold = JsonDocument.Parse(await SettingAsync(restored.ConnectionString, NumberSeriesStartupCheck.OutboxHoldSetting)))
            {
                hold.RootElement.GetProperty("reason").GetString().ShouldBe(NumberSeriesStartupCheck.RestoreHoldReason);
                hold.RootElement.GetProperty("since").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(restoredAt);
                hold.RootElement.GetProperty("backupTakenAt").ValueKind.ShouldBe(JsonValueKind.Null);
            }

            // What System health says of it.
            var record = JsonSerializer.Deserialize<ServerRestore>(await SettingAsync(restored.ConnectionString, NumberSeriesStartupCheck.RestoreSetting), Web).ShouldNotBeNull();
            record.RestoredAt.ShouldBe(restoredAt);
            record.BackupFile.ShouldBe(backup);
            record.MarkWrittenAt.ShouldNotBeNull();
            var next = issued.Max(Sequence) + NumberSeriesHighWater.Margin + 1;
            var moved = record.Raised.Single(r => r.Key == bills.Key);
            moved.NextNumber.ShouldBe($"ITB{next:000000}");
            moved.WouldHaveBeen.ShouldBe("ITB000003", "the number the restored database would have issued again");
            RestoreHealthWarnings.Warning(record, record.StartedAt, null, mark.Path, mark.MirrorPath).Message
                .ShouldContain($"{bills.Key}, next ITB{next:000000} instead of ITB000003");

            await using (var start = OnDatabase(restored.ConnectionString))
            {
                Sequence(await start.ServiceProvider.GetRequiredService<INumberSeries>().NextAsync(bills, null, Ct)).ShouldBe(next);
            }
        }
        finally
        {
            if (backup is not null)
            {
                File.Delete(backup);
            }

            Directory.Delete(machine.Root, recursive: true);
        }
    }

    /// <summary>An internal series of the test's own: hidden ones need no module to declare them.</summary>
    private static NumberSeriesDefinition Hidden(string prefix, string suffix) =>
        new($"it-{prefix.ToLowerInvariant()}-{suffix}", "IT restore", "Tests", prefix + "{SEQ:6}", NumberResetPolicy.Never, MaxLength: 24)
        {
            PerFacility = false,
            Hidden = true,
        };

    private static long Sequence(string number) => long.Parse(number[3..], CultureInfo.InvariantCulture);

    /// <summary>A data folder of the test's own, for the mark and the restore marker: the server's own mark is left alone.</summary>
    private static DataDirectories NewMachine(string suffix)
    {
        var machine = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "high-water-" + suffix) });
        machine.EnsureCreated();
        return machine;
    }

    private NumberSeriesHighWaterMark Mark(DataDirectories machine) =>
        new(machine, Microsoft.Extensions.Options.Options.Create(new PlatformOptions { DataDirectory = machine.Root }),
            server.Services.GetRequiredService<AuditSealKey>(), NullLogger<NumberSeriesHighWaterMark>.Instance);

    /// <summary>What the job does every minute, into <paramref name="mark"/>.</summary>
    private async Task<bool> RecordAsync(NumberSeriesHighWaterMark mark)
    {
        using var job = ActivatorUtilities.CreateInstance<NumberSeriesHighWaterJob>(server.Services, mark);
        return await job.RecordAsync(Ct);
    }

    /// <summary>
    /// What start-up does as the database is brought up to date, with the test's mark and data folder, on the test server's
    /// database or on <paramref name="database"/>: the settings service and the audit trail in the scope write there too, and
    /// a restore is anchored in the test folder's anchor file, not the test server's.
    /// </summary>
    private async Task StartAsync(NumberSeriesHighWaterMark mark, DataDirectories machine, string? database = null)
    {
        await using var scope = database is null ? server.Services.CreateAsyncScope() : OnDatabase(database);
        var check = ActivatorUtilities.CreateInstance<NumberSeriesStartupCheck>(scope.ServiceProvider, mark,
            new RestoreMarker(machine, TimeProvider.System, NullLogger<RestoreMarker>.Instance),
            new AuditRestores(new AuditAnchors(machine, NullLogger<AuditAnchors>.Instance), server.Services.GetRequiredService<AuditSealKey>(),
                NullLogger<AuditRestores>.Instance));
        await check.SeedAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), Ct);
        scope.ServiceProvider.GetRequiredService<NumberSeriesStartupStatus>().Failure.ShouldBeNull();
    }

    /// <summary>
    /// A scope of the test server whose database is <paramref name="connectionString"/>: every service in it reads and writes
    /// there, the settings service and the number series included.
    /// </summary>
    private AsyncServiceScope OnDatabase(string connectionString)
    {
        var scope = server.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.SetConnectionString(connectionString);
        return scope;
    }

    private async Task<List<string>> IssueAsync(NumberSeriesDefinition series, int count)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var numbers = scope.ServiceProvider.GetRequiredService<INumberSeries>();
        var issued = new List<string>();
        for (var i = 0; i < count; i++)
        {
            issued.Add(await numbers.NextAsync(series, null, Ct));
        }

        return issued;
    }

    private static async Task<NumberSeries> RowAsync(IServiceProvider services, string key)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NumberSeries>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.Key == key, Ct);
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it, and the recovery key that opens it.</summary>
    private async Task<(string File, byte[] RecoveryKey)> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
        run.Status.ShouldBe(BackupStatus.Succeeded, run.Error);
        return (Path.Combine(server.Services.GetRequiredService<DataDirectories>().Backups, run.FileName!),
            server.Services.GetRequiredService<RecoveryKeyStore>().Current().Key);
    }

    /// <summary>The script's first step: the server CLI's <c>backup decrypt</c> writes the database dump.</summary>
    private static async Task<string> DecryptAsync(string backup, byte[] recoveryKey, DataDirectories machine)
    {
        var dump = Path.Combine(machine.Temp, "restore.dump");
        Environment.SetEnvironmentVariable("HC360_RECOVERY_KEY", BackupRecoveryKey.Format(recoveryKey));
        try
        {
            (await ServerCli.RunAsync(["backup", "decrypt", "--in", backup, "--out", dump, "--data-dir", machine.Root])).ShouldBe(0, "backup decrypt");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HC360_RECOVERY_KEY", null);
        }

        return dump;
    }

    /// <summary>A hospital-wide setting's stored JSON, read from the database itself rather than through the settings service's cache.</summary>
    private static async Task<string> SettingAsync(string connectionString, string key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT value::text FROM platform.system_settings WHERE key = @key AND facility_id IS NULL", connection);
        command.Parameters.AddWithValue("key", key);
        return (string)(await command.ExecuteScalarAsync(Ct)).ShouldNotBeNull($"the setting {key}");
    }
}
