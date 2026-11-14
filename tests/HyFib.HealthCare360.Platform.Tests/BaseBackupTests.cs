using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
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
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS REL-02: while PostgreSQL archives the WAL, HC360 takes a base backup every week, and at once when there is none of the
/// database's current timeline, so a restore can reach any moment, not only last night's backup. Each is pg_basebackup's
/// copy, encrypted with the backup recovery key like a nightly backup, read back before it is kept, and the plain copy never
/// outlives the encrypted one. The CI Windows job sets up the account it signs in as against a real PostgreSQL
/// (tests/deploy/Test-StandbyReplication.ps1).
/// </summary>
public sealed class BaseBackupTests : IDisposable
{
    private const string SystemId = "7412345678901234567";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 22, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly RecoveryKeyStore _recoveryKeys;
    private readonly FakeBaseBackupTool _tool = new();

    public BaseBackupTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        var protection = new EphemeralDataProtectionProvider();
        _recoveryKeys = new RecoveryKeyStore(_directories, protection, new FieldProtector(_directories, protection), TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_base_backup_is_pg_basebackups_copy_encrypted_with_the_recovery_key_and_named_by_its_time_and_first_WAL_file()
    {
        var result = await Service().CreateAsync(Database(), TestContext.Current.CancellationToken);

        result.FileName.ShouldBe("hc360-base-20261115-220000-000000010000000000000042.hc360base");
        (result.SystemIdentifier, result.Timeline).ShouldBe((SystemId, 1));
        var file = Path.Combine(_directories.Backups, "base", SystemId, result.FileName);
        result.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken))));
        _tool.Label.ShouldBe("HC360 base backup 2026-11-15 22:00 UTC");

        var key = _recoveryKeys.Current();
        result.RecoveryKeyId.ShouldBe(key.KeyId);
        var output = Path.Combine(_root, "restored");
        var manifest = await BaseBackupArchive.DecryptToFolderAsync(file, key.Key, output, TestContext.Current.CancellationToken);

        (manifest.SystemIdentifier, manifest.Timeline, manifest.StartWalFile, manifest.Database, manifest.PostgresVersion)
            .ShouldBe((SystemId, 1, "000000010000000000000042", "hc360", "17.2"));
        manifest.Parts.Select(p => p.Name).ShouldBe(["backup_manifest", "base.tar.gz", "pg_wal.tar.gz"]);
        foreach (var (name, content) in _tool.Written)
        {
            (await File.ReadAllBytesAsync(Path.Combine(output, name), TestContext.Current.CancellationToken)).ShouldBe(content, name);
        }
    }

    [Fact]
    public async Task No_plain_copy_or_unfinished_file_is_left_behind_whether_the_copy_succeeds_or_fails()
    {
        await Service().CreateAsync(Database(), TestContext.Current.CancellationToken);
        Directory.EnumerateDirectories(_directories.Temp).ShouldBeEmpty();
        Directory.EnumerateFiles(Path.Combine(_directories.Backups, "base", SystemId), "*.partial").ShouldBeEmpty();

        _tool.FailAfterWriting = true;
        await Should.ThrowAsync<InvalidOperationException>(() => Service().CreateAsync(Database(), TestContext.Current.CancellationToken));
        Directory.EnumerateDirectories(_directories.Temp).ShouldBeEmpty("the plain copy pg_basebackup began is removed");
        Directory.EnumerateFiles(Path.Combine(_directories.Backups, "base", SystemId)).Count().ShouldBe(1, "only the first base backup is there");
    }

    [Fact]
    public async Task A_base_backup_opens_only_with_the_recovery_key_current_when_it_was_made()
    {
        var result = await Service().CreateAsync(Database(), TestContext.Current.CancellationToken);
        var file = Path.Combine(_directories.Backups, "base", SystemId, result.FileName);

        var output = Path.Combine(_root, "restored");
        await Should.ThrowAsync<BackupKeyMismatchException>(() => BaseBackupArchive.DecryptToFolderAsync(file, BackupRecoveryKey.Create(), output, TestContext.Current.CancellationToken));
        Directory.EnumerateFiles(output).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_damaged_base_backup_is_refused_and_leaves_none_of_its_files_where_it_was_decrypted()
    {
        var result = await Service().CreateAsync(Database(), TestContext.Current.CancellationToken);
        var file = Path.Combine(_directories.Backups, "base", SystemId, result.FileName);
        var bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        bytes[^40] ^= 0x01;
        await File.WriteAllBytesAsync(file, bytes, TestContext.Current.CancellationToken);

        var output = Path.Combine(_root, "restored");
        await Should.ThrowAsync<CryptographicException>(() => BaseBackupArchive.DecryptToFolderAsync(file, _recoveryKeys.Current().Key, output, TestContext.Current.CancellationToken));
        Directory.EnumerateFiles(output).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_base_backup_is_not_decrypted_over_files_already_in_the_folder()
    {
        var result = await Service().CreateAsync(Database(), TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "restored");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "base.tar.gz"), "someone else's", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<IOException>(() => BaseBackupArchive.DecryptToFolderAsync(
            Path.Combine(_directories.Backups, "base", SystemId, result.FileName), _recoveryKeys.Current().Key, output, TestContext.Current.CancellationToken));
        (await File.ReadAllTextAsync(Path.Combine(output, "base.tar.gz"), TestContext.Current.CancellationToken)).ShouldBe("someone else's");
    }

    [Fact]
    public async Task An_archive_whose_files_do_not_match_its_manifest_is_refused()
    {
        var parts = Path.Combine(_root, "parts");
        Directory.CreateDirectory(parts);
        var data = RandomNumberGenerator.GetBytes(100);
        await File.WriteAllBytesAsync(Path.Combine(parts, "base.tar.gz"), data, TestContext.Current.CancellationToken);
        var manifest = new BaseBackupManifest(BaseBackupArchive.Format, Now, "1.0.0", "17.2", "hc360", SystemId, 1, "000000010000000000000042",
            [new BaseBackupPart("base.tar.gz", 100)]);
        using var archive = new MemoryStream();
        await BaseBackupArchive.WriteAsync(archive, manifest, parts, TestContext.Current.CancellationToken);
        archive.Position = 0;
        var read = await BaseBackupArchive.ReadAsync(archive, null, TestContext.Current.CancellationToken);
        (read.SystemIdentifier, read.StartWalFile, read.Parts.Single()).ShouldBe((SystemId, "000000010000000000000042", new BaseBackupPart("base.tar.gz", 100)));

        var missing = manifest with { Parts = [new BaseBackupPart("base.tar.gz", 100), new BaseBackupPart("pg_wal.tar.gz", 10)] };
        var incomplete = await HandWrittenAsync(missing, ("pgdata/base.tar.gz", data));
        (await Should.ThrowAsync<InvalidDataException>(() => BaseBackupArchive.ReadAsync(incomplete, null, TestContext.Current.CancellationToken)))
            .Message.ShouldContain("incomplete");

        var extra = await HandWrittenAsync(manifest, ("pgdata/base.tar.gz", data), ("pgdata/other.bin", data));
        await Should.ThrowAsync<InvalidDataException>(() => BaseBackupArchive.ReadAsync(extra, null, TestContext.Current.CancellationToken));

        var resized = await HandWrittenAsync(manifest, ("pgdata/base.tar.gz", data[..50]));
        await Should.ThrowAsync<InvalidDataException>(() => BaseBackupArchive.ReadAsync(resized, null, TestContext.Current.CancellationToken));

        var escaping = await HandWrittenAsync(manifest with { Parts = [new BaseBackupPart("../base.tar.gz", 100)] }, ("pgdata/../base.tar.gz", data));
        await Should.ThrowAsync<InvalidDataException>(() => BaseBackupArchive.ReadAsync(escaping, Path.Combine(_root, "out"), TestContext.Current.CancellationToken));
        File.Exists(Path.Combine(_root, "base.tar.gz")).ShouldBeFalse("nothing is written outside the folder");
    }

    [Fact]
    public void Base_backups_past_the_retention_days_go_but_the_newest_always_stays()
    {
        _options.Backup.RetentionDays = 30;
        var folder = Path.Combine(_directories.Backups, "base", SystemId);
        Directory.CreateDirectory(folder);
        foreach (var at in new[] { Now.AddDays(-60), Now.AddDays(-45), Now.AddDays(-20) })
        {
            File.WriteAllBytes(Path.Combine(folder, BaseBackupArchive.FileName(at, "000000010000000000000042")), []);
        }

        Service().RemoveOld(folder);

        BaseBackupArchive.List(folder).Select(f => f.CreatedAt).ShouldBe([Now.AddDays(-20)]);

        // Archiving stopped for months: the one base backup left is kept, however old.
        File.Delete(BaseBackupArchive.List(folder).Single().Path);
        File.WriteAllBytes(Path.Combine(folder, BaseBackupArchive.FileName(Now.AddDays(-200), "000000010000000000000042")), []);
        Service().RemoveOld(folder);
        BaseBackupArchive.List(folder).Count.ShouldBe(1);
    }

    [Fact]
    public void A_base_backup_file_name_says_when_it_was_made_and_its_timeline()
    {
        var described = BaseBackupArchive.Describe(Path.Combine(_root, "base", "1", "hc360-base-20261115-220000-000000020000000000000042.hc360base")).ShouldNotBeNull();

        described.CreatedAt.ShouldBe(Now);
        (described.StartWalFile, described.Timeline).ShouldBe(("000000020000000000000042", 2));
        BaseBackupArchive.Describe("hc360-20261115-013000-scheduled.hc360bak").ShouldBeNull();
        BaseBackupArchive.Describe("hc360-base-20261115-220000-00000002000000000000004g.hc360base").ShouldBeNull();
        BaseBackupArchive.Describe("hc360-base-20261115-220000-000000020000000000000042.hc360base.partial").ShouldBeNull();
    }

    [Fact]
    public void A_base_backup_is_due_at_once_when_there_is_none_of_the_current_timeline()
    {
        var zone = HospitalTimeZone.Zone;
        BaseBackupSchedule.Due(null, 1, null, Now, zone).ShouldBeTrue("archiving was just turned on");

        var beforeFailover = new BaseBackupFile("x", Now.AddHours(-1), "000000010000000000000042");
        BaseBackupSchedule.Due(beforeFailover, 2, null, Now, zone).ShouldBeTrue("a promoted standby runs on a new timeline");
        BaseBackupSchedule.Due(beforeFailover, 1, null, Now, zone).ShouldBeFalse();
    }

    [Fact]
    public void A_base_backup_is_due_every_Sunday_at_half_past_three_in_the_hospitals_time()
    {
        var zone = HospitalTimeZone.Zone;
        // Sunday 15 November, 03:31 IST.
        var newest = new BaseBackupFile("x", new DateTimeOffset(2026, 11, 14, 22, 1, 0, TimeSpan.Zero), "000000010000000000000042");

        BaseBackupSchedule.Due(newest, 1, null, new DateTimeOffset(2026, 11, 21, 21, 59, 0, TimeSpan.Zero), zone).ShouldBeFalse("Sunday 22 November, 03:29 IST");
        BaseBackupSchedule.Due(newest, 1, null, new DateTimeOffset(2026, 11, 21, 22, 0, 0, TimeSpan.Zero), zone).ShouldBeTrue("Sunday 22 November, 03:30 IST");
    }

    [Fact]
    public void A_failed_base_backup_is_tried_again_after_six_hours_not_every_quarter_of_an_hour()
    {
        var zone = HospitalTimeZone.Zone;
        BaseBackupSchedule.Due(null, 1, Now.AddHours(-5), Now, zone).ShouldBeFalse();
        BaseBackupSchedule.Due(null, 1, Now.AddHours(-6), Now, zone).ShouldBeTrue();
    }

    [Fact]
    public void Pg_basebackup_signs_in_as_the_base_backup_account_without_a_password_and_streams_the_WAL_it_needs()
    {
        var arguments = PgBaseBackupTool.Arguments(@"C:\ProgramData\HyFib\HealthCare360\temp\basebackup-1", "HC360 base backup", "localhost", 5432);

        arguments.ShouldBe(
        [
            @"--pgdata=C:\ProgramData\HyFib\HealthCare360\temp\basebackup-1", "--format=tar", "--gzip", "--wal-method=stream", "--checkpoint=fast",
            "--label=HC360 base backup", "--no-password", "--host=localhost", "--port=5432", "--username=hc360_basebackup",
        ]);
    }

    [Fact]
    public void System_health_says_nothing_while_the_WAL_is_not_archived_or_the_base_backups_are_made()
    {
        SystemHealthArchive.Warnings(new ArchiveReport(false, null, null, new()), Now).ShouldBeEmpty();

        var fresh = new BaseBackupFile("x", Now.AddDays(-3), "000000010000000000000042");
        SystemHealthArchive.Warnings(Report(fresh, new()), Now).ShouldBeEmpty();
    }

    [Fact]
    public void System_health_warns_while_there_is_no_base_backup_of_the_current_timeline()
    {
        var none = SystemHealthArchive.Warnings(Report(null, new()), Now).ShouldHaveSingleItem();
        (none.Code, none.Severity).ShouldBe(("pitr.no_base_backup", "warning"));

        var beforeFailover = new BaseBackupFile("x", Now.AddDays(-1), "000000010000000000000042");
        var report = Report(beforeFailover, new()) with { Cluster = new DatabaseCluster(SystemId, "000000020000000000000050") };
        var afterFailover = SystemHealthArchive.Warnings(report, Now).ShouldHaveSingleItem();
        afterFailover.Code.ShouldBe("pitr.no_base_backup");
        afterFailover.Message.ShouldContain("timeline 2");
    }

    [Fact]
    public void System_health_says_why_the_last_base_backup_failed_until_one_succeeds()
    {
        var newest = new BaseBackupFile("x", Now.AddDays(-7), "000000010000000000000042");
        var failed = new PointInTimeRecoveryStatus { BaseBackupFailedAt = Now.AddHours(-2), BaseBackupError = "pg_basebackup exited with code 1: no pg_hba.conf entry." };

        var warning = SystemHealthArchive.Warnings(Report(newest, failed), Now).ShouldHaveSingleItem();
        (warning.Code, warning.Severity).ShouldBe(("pitr.base_backup_failed", "warning"));
        warning.Message.ShouldContain("no pg_hba.conf entry");

        var since = new BaseBackupFile("x", Now.AddHours(-1), "000000010000000000000042");
        SystemHealthArchive.Warnings(Report(since, failed), Now).ShouldBeEmpty("a base backup succeeded since");
    }

    [Fact]
    public void System_health_warns_when_the_weekly_base_backup_has_not_been_made_for_over_a_week()
    {
        var old = new BaseBackupFile("x", Now.AddDays(-9), "000000010000000000000042");

        var warning = SystemHealthArchive.Warnings(Report(old, new()), Now).ShouldHaveSingleItem();
        (warning.Code, warning.Severity).ShouldBe(("pitr.base_backup_stale", "warning"));
        SystemHealthArchive.Warnings(Report(old with { CreatedAt = Now.AddDays(-7.5) }, new()), Now).ShouldBeEmpty();
    }

    private static ArchiveReport Report(BaseBackupFile? newest, PointInTimeRecoveryStatus status) =>
        new(true, new DatabaseCluster(SystemId, "000000010000000000000060"), newest, status);

    private static ArchivedDatabase Database() => new(new DatabaseCluster(SystemId, "000000010000000000000042"), "17.2", "hc360", 1024);

    private BaseBackupService Service() => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _recoveryKeys,
        _tool,
        Substitute.For<INotificationService>(),
        new FixedTime(Now),
        NullLogger<BaseBackupService>.Instance);

    /// <summary>Writes the three files pg_basebackup writes in tar format, and can fail once it has begun.</summary>
    private sealed class FakeBaseBackupTool : IBaseBackupTool
    {
        public Dictionary<string, byte[]> Written { get; } = [];

        public string? Label { get; private set; }

        public bool FailAfterWriting { get; set; }

        public async Task CopyClusterAsync(string targetFolder, string label, CancellationToken ct)
        {
            Label = label;
            Written.Clear();
            Written["base.tar.gz"] = RandomNumberGenerator.GetBytes(5000);
            Written["pg_wal.tar.gz"] = RandomNumberGenerator.GetBytes(300);
            Written["backup_manifest"] = Encoding.UTF8.GetBytes("""{ "PostgreSQL-Backup-Manifest-Version": 1 }""");
            foreach (var (name, content) in Written)
            {
                await File.WriteAllBytesAsync(Path.Combine(targetFolder, name), content, ct);
            }

            if (FailAfterWriting)
            {
                throw new InvalidOperationException("pg_basebackup exited with code 1: could not connect to server.");
            }
        }
    }

    /// <summary>An archive with <paramref name="manifest"/> and exactly the entries given, whatever the manifest lists.</summary>
    private static async Task<MemoryStream> HandWrittenAsync(BaseBackupManifest manifest, params (string Name, byte[] Content)[] entries)
    {
        var archive = new MemoryStream();
        await using (var tar = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, BaseBackupArchive.ManifestEntry) { DataStream = new MemoryStream(json) });
            foreach (var (name, content) in entries)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
            }
        }

        archive.Position = 0;
        return archive;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
