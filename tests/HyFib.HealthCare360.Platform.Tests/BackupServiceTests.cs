using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
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

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePostgres _postgres = new();
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly Auditing.AuditSealKey _sealKey;
    private readonly FieldProtector _protector;
    private readonly RecoveryKeyStore _recoveryKeys;

    public BackupServiceTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _options.Backup.SecondaryDirectory = Path.Combine(_root, "nas");
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

    [Fact]
    public async Task The_backup_before_an_update_is_a_verified_archive_of_the_database_and_the_key()
    {
        var result = await Service().CreatePreMigrationAsync(["20260928102924_InitialCreate"], CancellationToken.None);

        result.Kind.ShouldBe(BackupKind.PreMigration);
        result.DocumentCount.ShouldBeNull();
        result.Warning.ShouldBeNull();
        result.SecondaryCopied.ShouldBeTrue();
        var file = Path.Combine(_directories.Backups, result.FileName);
        result.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        File.ReadAllBytes(Path.Combine(_options.Backup.SecondaryDirectory!, result.FileName)).ShouldBe(File.ReadAllBytes(file));

        // pg_restore was given the very dump that pg_dump wrote.
        _postgres.Validated.ShouldBe(_postgres.Dumped);

        await using var input = File.OpenRead(file);
        await using var payload = await BackupEncryption.OpenAsync(input, _protector.DeriveKey("backup"), CancellationToken.None);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None);
        contents.Manifest.Kind.ShouldBe("PreMigration");
        contents.Manifest.IncludesDocuments.ShouldBeFalse();
        contents.Manifest.Migrations.ShouldBe(["20260928102924_InitialCreate"]);
        contents.Manifest.Database.ShouldBe("hc360_test");
        contents.Manifest.RowCounts.ShouldBe(_postgres.Rows, "the rows counted in the dump's snapshot, for a test restore to check");
        contents.MasterKey.ShouldBe(_protector.ExportMasterKey());

        // No plaintext dump or unfinished file is left behind.
        Directory.EnumerateFiles(_directories.Temp).ShouldBeEmpty();
        Directory.EnumerateFiles(_directories.Backups, "*.partial").ShouldBeEmpty();
    }

    /// <summary>
    /// The USB disk is unplugged, or the NAS down: the backup is made and verified all the same, and only its copy fails.
    /// Its note says why, as System health reads it back (<see cref="BackupCopies"/>).
    /// </summary>
    [Fact]
    public async Task A_backup_whose_copy_cannot_be_made_is_kept_and_says_why()
    {
        var unplugged = Path.Combine(_root, "unplugged");
        await File.WriteAllTextAsync(unplugged, string.Empty, TestContext.Current.CancellationToken);
        _options.Backup.SecondaryDirectory = Path.Combine(unplugged, "HC360-Backups");

        var result = await Service().CreatePreMigrationAsync(["20260928102924_InitialCreate"], CancellationToken.None);

        result.SecondaryCopied.ShouldBeFalse();
        result.SecondaryDisk.ShouldBeNull("no disk took it");
        result.Warning.ShouldNotBeNull().ShouldStartWith(BackupService.CopyFailedNote);
        BackupCopies.Reason(result.Warning).ShouldNotBeNullOrWhiteSpace();
        File.Exists(Path.Combine(_directories.Backups, result.FileName)).ShouldBeTrue("the backup is kept on the server's disk");
        Directory.EnumerateFiles(_directories.Backups, "*.partial").ShouldBeEmpty();
    }

    /// <summary>
    /// An append-only share (DEPLOYMENT.md section 8) lets the server add copies but not delete the old ones, so ransomware
    /// on the server cannot delete them either: the copy is made, and its note says so and that the older copies were kept,
    /// never that the copy failed (which System health would then warn of).
    /// </summary>
    [Fact]
    public async Task A_copy_to_a_share_that_refuses_deletes_is_made_and_says_the_older_copies_were_kept()
    {
        var copiedIt = false;

        var (copied, note) = await BackupService.CopyToSecondary(
            () => { copiedIt = true; return Task.CompletedTask; },
            () => throw new UnauthorizedAccessException(@"Access to the path '\\NAS\HC360-Backups\hc360-20261014-013000-scheduled.hc360bak' is denied."),
            @"\\NAS\HC360-Backups", NullLogger.Instance);

        (copied, copiedIt).ShouldBe((true, true));
        note.ShouldNotBeNull().ShouldStartWith("Copied to the secondary directory; older copies there were not removed: Access to the path");
        note.ShouldNotContain(BackupService.CopyFailedNote);
        BackupCopies.Reason(note).ShouldBeNull();
    }

    /// <summary>
    /// Each copy names the disk it went to: the disk is given an id on its first copy, in a file it keeps, so the next
    /// backup to the same disk names the same id, and a swapped disk at the same folder another (<see cref="BackupDisk"/>).
    /// </summary>
    [Fact]
    public async Task A_copy_names_the_disk_it_went_to()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow.AddMinutes(-1), includeDocuments: true, [], ct);
        var second = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow, includeDocuments: true, [], ct);

        first.SecondaryDisk.ShouldNotBeNull().ShouldMatch("^[0-9A-F]{8}$");
        second.SecondaryDisk.ShouldBe(first.SecondaryDisk);
        File.ReadAllText(Path.Combine(_options.Backup.SecondaryDirectory!, BackupDisk.MarkerFileName)).ShouldContain(first.SecondaryDisk);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_made_says_why_and_removes_nothing()
    {
        var removed = false;

        var (copied, note) = await BackupService.CopyToSecondary(
            () => throw new IOException("The device is not ready."),
            () => { removed = true; return new BackupPruneResult(0, 0, 0, 0, null); },
            @"E:\HC360-Backups", NullLogger.Instance);

        (copied, removed).ShouldBe((false, false));
        note.ShouldNotBeNull().ShouldStartWith(BackupService.CopyFailedNote);
        BackupCopies.Reason(note).ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// After each backup, both copies keep what grandfather-father-son retention keeps (<see cref="BackupRetention"/>): of
    /// two months of nightly backups, the newest days', a week's last and a month's last, on the server and the NAS alike,
    /// rather than every backup of the last 30 days and nothing older.
    /// </summary>
    [Fact]
    public async Task After_a_backup_both_copies_keep_what_retention_keeps()
    {
        var ct = TestContext.Current.CancellationToken;
        var secondary = _options.Backup.SecondaryDirectory!;
        Directory.CreateDirectory(secondary);
        var today = DateTimeOffset.UtcNow;
        var earlier = Enumerable.Range(1, 60)
            .Select(daysAgo => string.Create(CultureInfo.InvariantCulture, $"hc360-{today.AddDays(-daysAgo):yyyyMMdd}-200000-scheduled.hc360bak"))
            .ToList();
        foreach (var name in earlier)
        {
            File.WriteAllText(Path.Combine(_directories.Backups, name), name);
            File.WriteAllText(Path.Combine(secondary, name), name);
        }

        var result = await Service().CreateAsync(BackupKind.Manual, today, includeDocuments: true, [], ct);

        var all = earlier.Append(result.FileName).Select(n => BackupFile.From(n)!).ToList();
        var kept = all.Except(BackupRetention.ToRemove(all, _options.Backup.Retention, HospitalTimeZone.Zone, DateTimeOffset.UtcNow))
            .Select(b => b.Path).ToList();
        kept.Count.ShouldBeInRange(18, 26, "14 days, the weeks' and months' last backups");
        Directory.EnumerateFiles(_directories.Backups, "*.hc360bak").Select(Path.GetFileName).ShouldBe(kept, ignoreOrder: true);
        Directory.EnumerateFiles(secondary, "*.hc360bak").Select(Path.GetFileName).ShouldBe(kept, ignoreOrder: true);
    }

    [Fact]
    public async Task Documents_kept_because_a_list_is_missing_are_noted_and_are_not_a_failed_copy()
    {
        var (copied, note) = await BackupService.CopyToSecondary(
            () => Task.CompletedTask,
            () => new BackupPruneResult(1, 0, 0, 0, "hc360-20261101-013000-scheduled.hc360bak has no list of the documents it needs"),
            @"E:\HC360-Backups", NullLogger.Instance);

        copied.ShouldBeTrue();
        note.ShouldNotBeNull().ShouldStartWith(BackupService.UnusedKeptNote);
        BackupCopies.Reason(note).ShouldBeNull();
    }

    [Fact]
    public async Task A_document_saved_while_the_database_is_dumped_is_in_the_backup()
    {
        // The file store writes a document, then commits its row. A row that made it into the dump must find its file in
        // the backup, so the documents are listed after the dump.
        var document = Path.Combine(_directories.Files, "2026", "09", "saved-during-dump.bin");
        _postgres.DuringDump = () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(document)!);
            File.WriteAllBytes(document, [1, .. RandomNumberGenerator.GetBytes(40)]);
        };

        var ct = TestContext.Current.CancellationToken;
        var result = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow, includeDocuments: true, [], ct);

        result.DocumentCount.ShouldBe(1);
        await using var input = File.OpenRead(Path.Combine(_directories.Backups, result.FileName));
        await using var payload = await BackupEncryption.OpenAsync(input, _protector.DeriveKey("backup"), ct);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), ct);
        contents.Manifest.DocumentCount.ShouldBe(1);
        var listed = contents.Index.ShouldHaveSingleItem();
        listed.RelativePath.ShouldBe("2026/09/saved-during-dump.bin");
        var restored = Path.Combine(_root, "restored");
        await BackupBlobs.CopyAsync(_directories.Backups, contents, restored, ct);
        File.ReadAllBytes(Path.Combine(restored, "2026", "09", "saved-during-dump.bin")).ShouldBe(File.ReadAllBytes(document));
    }

    /// <summary>
    /// Every full backup carries the erasure ledger (DPDP Act 2023 s8(7)), inside its encryption, with the lines of the
    /// ledger's copy beside the second copy that the ledger lacks (one it lost with a disk), so a restore from any copy of the
    /// backup, the off-site one included, can carry out the erasures again. The backup before an update carries none: it is
    /// for going back on this machine, whose ledger the update leaves alone, with the version updated from, which reads an
    /// archive with nothing it does not know (F17).
    /// </summary>
    [Fact]
    public async Task A_backup_carries_the_erasure_ledger_with_what_its_copy_has_that_it_lost()
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = ErasureLedgerFiles.PathIn(_directories);
        Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
        await File.WriteAllTextAsync(ledger, "{\"id\":\"A\"}\n", ct);
        Directory.CreateDirectory(_options.Backup.SecondaryDirectory!);
        await File.WriteAllTextAsync(ErasureLedgerFiles.MirrorPath(_options.Backup)!, "{\"id\":\"A\"}\n{\"id\":\"B\"}\n", ct);

        var full = await ContentsAsync(await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow.AddMinutes(-1), includeDocuments: true, [], ct));
        Encoding.UTF8.GetString(full.ErasureLedger.ShouldNotBeNull()).ShouldBe("{\"id\":\"A\"}\n{\"id\":\"B\"}\n");
        full.Manifest.Format.ShouldBe(BackupArchive.Format);

        var beforeUpdate = await ContentsAsync(await Service().CreatePreMigrationAsync([], ct));
        beforeUpdate.Manifest.Format.ShouldBe(BackupArchive.InlineDocumentsFormat);
        (beforeUpdate.ErasureLedger, beforeUpdate.Manifest.ErasureLedger, beforeUpdate.Manifest.HospitalCa).ShouldBe(((byte[]?)null, (string?)null, (string?)null));

        async Task<BackupContents> ContentsAsync(BackupResult result)
        {
            await using var input = File.OpenRead(Path.Combine(_directories.Backups, result.FileName));
            await using var payload = await BackupEncryption.OpenAsync(input, _protector.DeriveKey("backup"), ct);
            return await BackupArchive.ReadAsync(payload, new BackupReadTargets(), ct);
        }
    }

    [Fact]
    public async Task A_backup_names_the_recovery_key_that_opens_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = BackupRecoveryKey.Original(_protector.ExportMasterKey());

        var result = await Service().CreatePreMigrationAsync([], ct);

        result.RecoveryKeyId.ShouldBe(BackupRecoveryKey.Id(original));
        var file = Path.Combine(_directories.Backups, result.FileName);
        await using (var input = File.OpenRead(file))
        await using (var payload = await BackupEncryption.OpenAsync(input, original, ct))
        {
            (payload.Version, payload.KeyId).ShouldBe((BackupEncryption.KeyedVersion, result.RecoveryKeyId));
        }

        // Another key is turned away before anything is decrypted, with the id of the key that opens the backup.
        var other = BackupRecoveryKey.Create();
        await using var again = File.OpenRead(file);
        var ex = await Should.ThrowAsync<BackupKeyMismatchException>(() => BackupEncryption.OpenAsync(again, other, ct));
        (ex.BackupKeyId, ex.GivenKeyId).ShouldBe((result.RecoveryKeyId, BackupRecoveryKey.Id(other)));
    }

    [Fact]
    public async Task After_a_new_recovery_key_backups_open_with_it_and_earlier_ones_with_the_key_before()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = BackupRecoveryKey.Original(_protector.ExportMasterKey());
        var before = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow.AddMinutes(-5), includeDocuments: true, [], ct);
        var admin = Guid.NewGuid();
        var created = _recoveryKeys.Create(admin);
        _recoveryKeys.Confirm(created.KeyId, created.Key, admin).Value.ShouldBe(before.RecoveryKeyId);
        BackupRecoveryKey.TryParse(created.Key, out var newKey, out _).ShouldBeTrue();

        var after = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow, includeDocuments: true, [], ct);

        after.RecoveryKeyId.ShouldBe(created.KeyId);
        await OpensWithAsync(after, newKey);
        await OpensWithAsync(before, original);
        await using var input = File.OpenRead(Path.Combine(_directories.Backups, after.FileName));
        await Should.ThrowAsync<BackupKeyMismatchException>(() => BackupEncryption.OpenAsync(input, original, ct));
    }

    /// <summary>
    /// A missed backup caught up while the hospital works runs pg_dump below normal priority, so the server's requests come
    /// first (<see cref="BackupCatchUpJob"/>); every other backup at normal priority, to be done within the night.
    /// </summary>
    [Fact]
    public async Task A_backup_taken_gently_runs_pg_dump_below_normal_priority()
    {
        var ct = TestContext.Current.CancellationToken;

        await Service().CreateAsync(BackupKind.Scheduled, DateTimeOffset.UtcNow, includeDocuments: true, [], lowPriority: true, ct);
        _postgres.LowPriority.ShouldBe(true);

        await Service().CreatePreMigrationAsync([], ct);
        _postgres.LowPriority.ShouldBe(false);
    }

    [Fact]
    public async Task A_backup_is_refused_when_the_disk_has_no_room_for_it()
    {
        _postgres.DatabaseSize = long.MaxValue / 4;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service().CreatePreMigrationAsync([], CancellationToken.None));

        ex.Message.ShouldContain("Not enough free disk space");
        _postgres.Dumped.ShouldBeNull();
        BackupFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_backup_whose_dump_cannot_be_read_back_is_discarded()
    {
        _postgres.DumpIsDamaged = true;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service().CreatePreMigrationAsync([], CancellationToken.None));

        ex.Message.ShouldContain("pg_restore cannot read");
        BackupFiles().ShouldBeEmpty();
        Directory.EnumerateFiles(_directories.Temp).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_pg_restore_the_backup_is_kept_with_a_note()
    {
        _postgres.HasPgRestore = false;

        var result = await Service().CreatePreMigrationAsync([], CancellationToken.None);

        result.Warning.ShouldNotBeNull().ShouldContain("without pg_restore");
        File.Exists(Path.Combine(_directories.Backups, result.FileName)).ShouldBeTrue();
    }

    private async Task OpensWithAsync(BackupResult backup, byte[] key)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var input = File.OpenRead(Path.Combine(_directories.Backups, backup.FileName));
        await using var payload = await BackupEncryption.OpenAsync(input, key, ct);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), ct);
        contents.MasterKey.ShouldBe(_protector.ExportMasterKey());
    }

    /// <summary>What a backup leaves in the backup folder: archives, unfinished ones and lists, not the folder's own marker and lock.</summary>
    private IEnumerable<string> BackupFiles() =>
        Directory.EnumerateFiles(_directories.Backups).Where(f => f.EndsWith(".hc360bak", StringComparison.Ordinal)
            || f.EndsWith(".partial", StringComparison.Ordinal) || f.EndsWith(".refs", StringComparison.Ordinal));

    private BackupService Service() => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _recoveryKeys,
        _protector,
        _sealKey,
        new NoDocumentsLock(),
        _postgres,
        Substitute.For<INotificationService>(),
        TimeProvider.System,
        NullLogger<BackupService>.Instance);

    private sealed class FakePostgres : IPostgresTools
    {
        public long DatabaseSize { get; set; } = 1024;

        public bool DumpIsDamaged { get; set; }

        public bool HasPgRestore { get; set; } = true;

        public byte[]? Dumped { get; private set; }

        public byte[]? Validated { get; private set; }

        /// <summary>Whether pg_dump was asked to run below normal priority.</summary>
        public bool? LowPriority { get; private set; }

        /// <summary>Something that happens on the server while pg_dump runs.</summary>
        public Action? DuringDump { get; set; }

        public string DatabaseName => "hc360_test";

        public Task<long> GetDatabaseSizeAsync(CancellationToken ct) => Task.FromResult(DatabaseSize);

        /// <summary>The key tables' rows in the dump's snapshot, as pg_dump's session counts them.</summary>
        public Dictionary<string, long> Rows { get; } = new(StringComparer.Ordinal) { ["patients.patients"] = 12_480, ["audit.audit_events"] = 3_904_117 };

        public async Task<IReadOnlyDictionary<string, long>> DumpAsync(string outputFile, bool lowPriority, CancellationToken ct)
        {
            LowPriority = lowPriority;
            DuringDump?.Invoke();
            Dumped = [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(4096)];
            await File.WriteAllBytesAsync(outputFile, Dumped, ct);
            return Rows;
        }

        public async Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct)
        {
            Validated = await File.ReadAllBytesAsync(dumpFile, ct);
            return DumpIsDamaged
                ? throw new InvalidOperationException("pg_restore cannot read the database dump (exit code 1): input file does not appear to be a valid archive")
                : HasPgRestore;
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
