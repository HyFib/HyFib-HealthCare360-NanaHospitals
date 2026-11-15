using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Test restores (DED-16): the newest backup restored into a scratch database, as a restore opens it, its key tables'
/// rows checked against those recorded when it was made, the scratch database dropped whatever happened, and the outcome
/// on the backup and on System health. The scratch database is a fake here; PostgresRestoreTests and the end-to-end
/// journey (BackupRestoreTestTests there) restore into real ones.
/// </summary>
public sealed class BackupRestoreTestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly FieldProtector _protector;
    private readonly RecoveryKeyStore _recoveryKeys;
    private readonly Auditing.AuditSealKey _sealKey;
    private readonly FakePostgres _postgres = new();
    private readonly FakeScratch _scratch = new();

    public BackupRestoreTestTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        var protection = new EphemeralDataProtectionProvider();
        _protector = new FieldProtector(_directories, protection);
        _recoveryKeys = new RecoveryKeyStore(_directories, protection, _protector, TimeProvider.System);
        _sealKey = new Auditing.AuditSealKey(_directories, protection);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The backup is opened with the server's recovery key, its dump (the one pg_dump wrote) restored, and the restored key
    /// tables hold the rows recorded in its manifest: it passes, the scratch database is dropped, and the plain dump is gone.
    /// </summary>
    [Fact]
    public async Task A_backup_that_restores_with_the_rows_it_recorded_passes_and_its_scratch_database_is_dropped()
    {
        var run = await BackupAsync();
        _scratch.Rows = new(_postgres.Rows);
        var saved = 0;

        var outcome = await Tester().TestAsync(run, _ => Task.FromResult(saved++), Ct);

        outcome.Passed.ShouldBeTrue(outcome.Note);
        run.RestoreTestPassed.ShouldBe(true);
        run.RestoreTestedAt.ShouldNotBeNull();
        saved.ShouldBe(1, "the outcome is saved on the backup");
        run.RestoreTestNote.ShouldNotBeNull().ShouldContain("its 2 key tables hold the 3,916,597 rows recorded when the backup was made");
        _scratch.Restored.ShouldBe(_postgres.Dumped, "the dump pg_dump wrote, read back out of the encrypted backup");
        _scratch.Dropped.ShouldBe(_scratch.Created);
        _scratch.RoomNeeded.ShouldBe((2 * _postgres.DatabaseSize) + (1L << 30), "room for the restored copy, as for a backup");
        Directory.EnumerateFiles(_directories.Temp).ShouldBeEmpty("no plain database dump is left behind");
    }

    /// <summary>A restored database that does not hold the rows the backup recorded fails, naming each table that differs.</summary>
    [Fact]
    public async Task A_restored_database_short_of_the_rows_recorded_fails_and_says_which_tables()
    {
        var run = await BackupAsync();
        _scratch.Rows = new(_postgres.Rows) { ["patients.patients"] = 12_479 };
        _scratch.Rows.Remove("audit.audit_events");

        var outcome = await Tester().TestAsync(run, _ => Task.CompletedTask, Ct);

        outcome.Passed.ShouldBeFalse();
        run.RestoreTestPassed.ShouldBe(false);
        run.RestoreTestNote.ShouldBe(
            "The restored database is not the one backed up: patients.patients has 12,479 rows, and had 12,480 when the backup was made; audit.audit_events is missing.");
        _scratch.Dropped.ShouldBe(_scratch.Created);
    }

    /// <summary>pg_restore stopping at an error fails the test with pg_restore's words, and the scratch database is dropped all the same.</summary>
    [Fact]
    public async Task A_restore_that_stops_at_an_error_fails_and_its_scratch_database_is_dropped_all_the_same()
    {
        var run = await BackupAsync();
        _scratch.RestoreFails = new ScratchDatabaseException("pg_restore stopped at an error (exit code 1): pg_restore: error: could not execute query: ERROR:  out of memory");

        var outcome = await Tester().TestAsync(run, _ => Task.CompletedTask, Ct);

        (outcome.Passed, outcome.Note).ShouldBe((false, "pg_restore stopped at an error (exit code 1): pg_restore: error: could not execute query: ERROR:  out of memory"));
        _scratch.Created.ShouldHaveSingleItem();
        _scratch.Dropped.ShouldBe(_scratch.Created);
        Directory.EnumerateFiles(_directories.Temp).ShouldBeEmpty();
    }

    /// <summary>
    /// The account cannot make the scratch database (no right to create databases, or no room on PostgreSQL's disk): the
    /// test fails, saying why and what to do, and nothing was restored, so nothing is left to drop.
    /// </summary>
    [Fact]
    public async Task A_test_that_cannot_make_its_scratch_database_fails_with_why_and_leaves_nothing()
    {
        var run = await BackupAsync();
        _scratch.CreateFails = new ScratchDatabaseException("The account test restores sign in as, hc360_app, may not create databases, so there is nowhere to restore to.");

        var outcome = await Tester().TestAsync(run, _ => Task.CompletedTask, Ct);

        outcome.Passed.ShouldBeFalse();
        run.RestoreTestNote.ShouldBe("The account test restores sign in as, hc360_app, may not create databases, so there is nowhere to restore to.");
        _scratch.Dropped.ShouldBeEmpty();
        Directory.EnumerateFiles(_directories.Temp).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_backup_no_longer_on_any_disk_fails_its_test()
    {
        var run = await BackupAsync();
        File.Delete(Path.Combine(_directories.Backups, run.FileName!));

        var outcome = await Tester().TestAsync(run, _ => Task.CompletedTask, Ct);

        (outcome.Passed, outcome.Note).ShouldBe((false, $"{run.FileName} is no longer on this server's disk, nor on the second disk or NAS, so it could not be restored."));
        _scratch.Created.ShouldBeEmpty();
    }

    /// <summary>
    /// A backup keeps its documents in its backup folder, which a restore from that folder needs: one gone from the folder
    /// fails the test, naming it, though the database restored; and the scratch database is dropped all the same.
    /// </summary>
    [Fact]
    public async Task A_backup_whose_folder_lost_a_document_it_lists_fails_its_test()
    {
        var path = Path.Combine(_directories.Files, "2026", "11", "scan.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, _protector.ProtectBytes(RandomNumberGenerator.GetBytes(500), "files"), Ct);
        var run = await BackupAsync();
        _scratch.Rows = new(_postgres.Rows);
        foreach (var blob in Directory.EnumerateFiles(Path.Combine(_directories.Backups, BackupStore.BlobsFolder), "*" + BackupStore.BlobExtension, SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }

        var outcome = await Tester().TestAsync(run, _ => Task.CompletedTask, Ct);

        outcome.Passed.ShouldBeFalse();
        outcome.Note.ShouldContain("1 of the backup's 1 documents have no copy in its backup folder");
        outcome.Note.ShouldContain("2026/11/scan.bin");
        _scratch.Restored.ShouldNotBeNull("the database was restored");
        _scratch.Dropped.ShouldBe(_scratch.Created);
    }

    /// <summary>A backup made before backups recorded their rows passes on restoring without error, and says it had nothing to count.</summary>
    [Fact]
    public async Task A_backup_made_before_row_counts_passes_on_restoring_without_error()
    {
        _postgres.Rows.Clear();
        var run = await BackupAsync();

        var outcome = await Tester().TestAsync(run, _ => Task.CompletedTask, Ct);

        outcome.Passed.ShouldBeTrue();
        outcome.Note.ShouldContain("The backup recorded no row counts to check them against");
        _scratch.Counted.ShouldBeFalse();
    }

    /// <summary>A scratch database that could not be dropped is said on the backup, and the next test drops it before making its own.</summary>
    [Fact]
    public async Task A_scratch_database_that_cannot_be_dropped_is_said_on_the_backup()
    {
        var run = await BackupAsync();
        _scratch.Rows = new(_postgres.Rows);
        _scratch.DropFails = new NpgsqlException("The connection was lost.");

        var outcome = await Tester().TestAsync(run, _ => Task.CompletedTask, Ct);

        outcome.Passed.ShouldBeTrue("the backup restored as it should");
        outcome.Note.ShouldEndWith($"The scratch database {_scratch.Created[0]} could not be dropped (The connection was lost.); the next test restore drops it.");
    }

    /// <summary>
    /// Weekly, from the nightly upkeep: due again at 03:30 a week after one passed at 03:45, not eight nights on; due every
    /// night while none passes; and always before the first.
    /// </summary>
    [Fact]
    public void A_test_restore_is_due_weekly_and_each_night_until_one_passes()
    {
        var passed = Ist(7, 3, 45);

        BackupRestoreTest.Due(passed, everyDays: 7, Ist(13, 3, 30)).ShouldBeFalse();
        BackupRestoreTest.Due(passed, everyDays: 7, Ist(14, 3, 30)).ShouldBeTrue();
        BackupRestoreTest.Due(null, everyDays: 7, Ist(14, 3, 30)).ShouldBeTrue();
        BackupRestoreTest.Due(passed, everyDays: 1, Ist(8, 3, 30)).ShouldBeTrue();
    }

    [Fact]
    public void The_differences_name_every_table_that_does_not_hold_its_rows()
    {
        var recorded = new Dictionary<string, long> { ["patients.patients"] = 10, ["billing.bills"] = 4 };

        BackupRowCounts.Differences(recorded, new Dictionary<string, long>(recorded)).ShouldBeNull();
        BackupRowCounts.Differences(recorded, new Dictionary<string, long> { ["patients.patients"] = 11 })
            .ShouldBe("The restored database is not the one backed up: patients.patients has 11 rows, and had 10 when the backup was made; billing.bills is missing.");
    }

    /// <summary>
    /// A restore fills PostgreSQL's disk with a second copy of the database, and a full disk stops the hospital's own: the
    /// test does not start without room, nor where HC360 cannot see the disk, and says what to do.
    /// </summary>
    [Fact]
    public void A_test_restore_does_not_start_without_room_on_PostgreSQLs_disk()
    {
        const long needed = 8L << 30;

        PostgresTools.RoomProblem(@"C:\Program Files\PostgreSQL\17\data", needed, _ => 40L << 30).ShouldBeNull();
        PostgresTools.RoomProblem(@"C:\Program Files\PostgreSQL\17\data", needed, _ => 3L << 30).ShouldNotBeNull()
            .ShouldStartWith(@"Not enough free space for the restored copy beside the hospital's database in C:\Program Files\PostgreSQL\17\data: 3,072 MB free, about 8,192 MB needed.");
        PostgresTools.RoomProblem(null, needed, _ => 40L << 30).ShouldNotBeNull().ShouldContain("pg_read_all_settings");
        PostgresTools.RoomProblem("/var/lib/postgresql/data", needed, _ => null).ShouldNotBeNull().ShouldContain("which is not on this server");
    }

    [Fact]
    public void System_health_is_silent_while_a_test_restore_passed_within_two_weeks()
    {
        var history = new RestoreTestHistory(Ist(1, 3, 40), Ist(1, 3, 40), true, "Restored without error.", "hc360-x.hc360bak", Ist(1, 1, 30).AddDays(-60));

        SystemHealthBackups.RestoreTestWarning(history, new BackupTestRestoreOptions(), Ist(15, 3, 0), HospitalTimeZone.Zone).ShouldBeNull();
    }

    /// <summary>Past two weeks without a test that passed, System health warns, with the last test's outcome.</summary>
    [Fact]
    public void System_health_warns_once_no_test_restore_has_passed_for_two_weeks_with_the_last_ones_outcome()
    {
        var history = new RestoreTestHistory(Ist(1, 3, 40), Ist(15, 3, 50), false, "pg_restore stopped at an error (exit code 1): disk full",
            "hc360-20261114-200000-scheduled.hc360bak", Ist(1, 1, 30).AddDays(-60));

        var warning = SystemHealthBackups.RestoreTestWarning(history, new BackupTestRestoreOptions(), Ist(15, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull();

        (warning.Code, warning.Severity).ShouldBe((SystemHealthBackups.RestoreTestCode, "warning"));
        warning.Message.ShouldStartWith("No backup has passed a test restore since 01 Nov 2026 03:40, so nothing shows the backups would restore.");
        warning.Message.ShouldContain(
            "The last test, of hc360-20261114-200000-scheduled.hc360bak on 15 Nov 2026 03:50, failed: pg_restore stopped at an error (exit code 1): disk full.");

        var never = history with { LastPassedAt = null };
        SystemHealthBackups.RestoreTestWarning(never, new BackupTestRestoreOptions(), Ist(15, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull()
            .Message.ShouldStartWith("No backup has passed a test restore yet");
    }

    /// <summary>
    /// None tried yet: a new installation has two weeks of nights to try one before System health warns; on an older one
    /// it warns at once, as the test restores evidently do not run.
    /// </summary>
    [Fact]
    public void Before_any_test_restore_System_health_waits_while_the_backups_are_new()
    {
        var fresh = new RestoreTestHistory(null, null, null, null, null, Ist(10, 1, 30));
        SystemHealthBackups.RestoreTestWarning(fresh, new BackupTestRestoreOptions(), Ist(15, 9, 0), HospitalTimeZone.Zone).ShouldBeNull();

        var old = fresh with { OldestBackupAt = Ist(1, 1, 30).AddDays(-30) };
        SystemHealthBackups.RestoreTestWarning(old, new BackupTestRestoreOptions(), Ist(15, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull()
            .Message.ShouldStartWith("No backup has been test-restored yet");
    }

    [Fact]
    public void Test_restores_switched_off_are_a_warning()
    {
        var history = new RestoreTestHistory(Ist(14, 3, 40), Ist(14, 3, 40), true, null, null, null);

        SystemHealthBackups.RestoreTestWarning(history, new BackupTestRestoreOptions { Enabled = false }, Ist(15, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull()
            .Message.ShouldContain("HealthCare360:Backup:TestRestore:Enabled is false");
    }

    /// <summary>A copy that no longer reads back as made, and could not be mended, is named on System health while it is read back so.</summary>
    [Fact]
    public void A_damaged_copy_is_named_on_System_health_while_it_is_read_back_damaged()
    {
        var damaged = new BackupCopy("7F3A2C1D", Ist(2, 1, 40), Ist(14, 3, 50), "It no longer matches the SHA-256 the backup was verified with.");
        var good = new BackupCopy("7F3A2C1D", Ist(3, 1, 40), Ist(14, 3, 52));

        var warning = SystemHealthBackups.DamagedCopiesWarning([("hc360-a.hc360bak", damaged), ("hc360-b.hc360bak", good)], 7, Ist(15, 9, 0)).ShouldNotBeNull();

        (warning.Code, warning.Severity).ShouldBe((SystemHealthBackups.CopyDamagedCode, "warning"));
        warning.Message.ShouldStartWith("A copy on the second disk or NAS no longer read back as made: hc360-a.hc360bak on disk 7F3A2C1D.");
        SystemHealthBackups.DamagedCopiesWarning([("hc360-a.hc360bak", damaged)], 7, Ist(14, 3, 50).AddDays(15)).ShouldBeNull("removed by retention, so no longer read back");
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it, in the backup history.</summary>
    private async Task<BackupRun> BackupAsync()
    {
        var service = new BackupService(Substitute.For<IDbContextFactory<HospitalDbContext>>(), Options.Create(_options), _directories, _recoveryKeys, _protector,
            _sealKey, new NoDocumentsLock(), _postgres, Substitute.For<INotificationService>(), TimeProvider.System, NullLogger<BackupService>.Instance);
        var result = await service.CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow, includeDocuments: true, [], Ct);
        return new BackupRun
        {
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            Kind = result.Kind,
            Status = BackupStatus.Succeeded,
            FileName = result.FileName,
            SizeBytes = result.SizeBytes,
            Sha256 = result.Sha256,
            VerifiedAt = result.VerifiedAt,
            FormatVersion = BackupEncryption.KeyedVersion,
            RecoveryKeyId = result.RecoveryKeyId,
        };
    }

    private BackupRestoreTest Tester() => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _recoveryKeys,
        _postgres,
        _scratch,
        Substitute.For<INotificationService>(),
        TimeProvider.System,
        NullLogger<BackupRestoreTest>.Instance);

    private static DateTimeOffset Ist(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5));

    private sealed class FakePostgres : IPostgresTools
    {
        public long DatabaseSize { get; } = 64L << 20;

        public byte[]? Dumped { get; private set; }

        public Dictionary<string, long> Rows { get; } = new(StringComparer.Ordinal) { ["patients.patients"] = 12_480, ["audit.audit_events"] = 3_904_117 };

        public string DatabaseName => "hc360";

        public Task<long> GetDatabaseSizeAsync(CancellationToken ct) => Task.FromResult(DatabaseSize);

        public async Task<IReadOnlyDictionary<string, long>> DumpAsync(string outputFile, bool lowPriority, CancellationToken ct)
        {
            Dumped = [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(2048)];
            await File.WriteAllBytesAsync(outputFile, Dumped, ct);
            return new Dictionary<string, long>(Rows);
        }

        public Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>A scratch database in name only: what was restored into it, the rows it is given to hold, and its drop.</summary>
    private sealed class FakeScratch : IScratchDatabase
    {
        public Dictionary<string, long> Rows { get; set; } = [];

        public Exception? CreateFails { get; set; }

        public Exception? RestoreFails { get; set; }

        public Exception? DropFails { get; set; }

        public List<string> Created { get; } = [];

        public List<string> Dropped { get; } = [];

        public byte[]? Restored { get; private set; }

        public long RoomNeeded { get; private set; }

        public bool Counted { get; private set; }

        public Task<string> CreateAsync(long roomNeeded, CancellationToken ct)
        {
            RoomNeeded = roomNeeded;
            if (CreateFails is not null)
            {
                throw CreateFails;
            }

            var name = PostgresTools.ScratchPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            Created.Add(name);
            return Task.FromResult(name);
        }

        public async Task RestoreAsync(string database, string dumpFile, CancellationToken ct)
        {
            Created.ShouldContain(database);
            Restored = await File.ReadAllBytesAsync(dumpFile, ct);
            if (RestoreFails is not null)
            {
                throw RestoreFails;
            }
        }

        public Task<IReadOnlyDictionary<string, long>> CountRowsAsync(string database, IReadOnlyCollection<string> tables, CancellationToken ct)
        {
            Counted = true;
            return Task.FromResult<IReadOnlyDictionary<string, long>>(Rows.Where(r => tables.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value));
        }

        public Task DropAsync(string database, CancellationToken ct)
        {
            Dropped.Add(database);
            return DropFails is null ? Task.CompletedTask : Task.FromException(DropFails);
        }
    }

    private sealed class NoDocumentsLock : IDocumentStoreLock
    {
        public Task<IAsyncDisposable> HoldSharedAsync(CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(new Released());

        private sealed class Released : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
