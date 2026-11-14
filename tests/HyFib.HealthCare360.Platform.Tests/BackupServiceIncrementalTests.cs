using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Nightly backups that copy only new documents (docs/design/incremental-backup.md): each night is a real backup run
/// against real folders, with documents encrypted as the file store encrypts them. The F numbers are the failure model's.
/// </summary>
public sealed class BackupServiceIncrementalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly FieldProtector _protector;
    private readonly RecoveryKeyStore _recoveryKeys;
    private readonly Auditing.AuditSealKey _sealKey;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private int _documents;

    public BackupServiceIncrementalTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _options.Backup.SecondaryDirectory = Path.Combine(_root, "usb");
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

    private string Usb => _options.Backup.SecondaryDirectory!;

    [Fact]
    public async Task A_second_backup_copies_only_the_new_documents()
    {
        AddDocuments(3, 10_000);
        var first = await NightAsync(-1);
        BlobBytes(_directories.Backups).ShouldBe(DocumentBytes());

        var added = AddDocument(10_000);
        var second = await NightAsync(0);

        (first.DocumentCount, second.DocumentCount).ShouldBe((3, 4));
        BlobCount(_directories.Backups).ShouldBe(4);
        BlobBytes(_directories.Backups).ShouldBe(DocumentBytes());
        (await ContentsAsync(_directories.Backups, second)).Index.Select(d => d.RelativePath).ShouldContain(added);
        (await ProblemsAsync(_directories.Backups, first)).ShouldBeEmpty();
        (await ProblemsAsync(_directories.Backups, second)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_backup_folder_grows_by_the_new_documents_and_one_archive_a_night()
    {
        // The brief's "done when": growth of about (new documents + one dump) a night, not (all documents + dump).
        AddDocuments(20, 20_000);
        await NightAsync(-21);
        var previous = FolderBytes(_directories.Backups);
        long archives = 0;
        for (var night = -20; night <= 0; night++)
        {
            var added = new FileInfo(Path.Combine(_directories.Files, AddDocument(20_000)));
            var result = await NightAsync(night);
            var size = FolderBytes(_directories.Backups);
            var archive = result.SizeBytes;
            var list = new FileInfo(Path.ChangeExtension(Path.Combine(_directories.Backups, result.FileName), ".refs")).Length;

            // The new document, the night's archive and its list; the hash cache changes by a few dozen bytes.
            (size - previous).ShouldBeInRange(added.Length + archive + list, added.Length + archive + list + 1_024);
            previous = size;
            archives += archive + list;
        }

        // 41 documents of 20 kB and 22 archives: what a full copy each night would have taken is 22 times the documents.
        BlobBytes(_directories.Backups).ShouldBe(DocumentBytes());
        FolderBytes(_directories.Backups).ShouldBeLessThan(DocumentBytes() + archives + new FileInfo(Path.Combine(_directories.Backups, BackupHashCache.FileName)).Length + 20_000);
        BlobBytes(Usb).ShouldBe(DocumentBytes());
    }

    [Fact]
    public async Task A_usb_disk_away_for_a_week_catches_up_and_restores_on_its_own()
    {
        // F3: the disk at the server misses nights; every archive on it must still restore from it alone.
        AddDocuments(5, 8_000);
        var onDisk = await NightAsync(-6);
        var away = Path.Combine(_root, "usb-off-site");
        Directory.Move(Usb, away);
        var unplugged = Path.Combine(_root, "no-disk");
        await File.WriteAllTextAsync(unplugged, string.Empty, Ct);
        var usb = Usb;
        _options.Backup.SecondaryDirectory = Path.Combine(unplugged, "HC360-Backups");
        for (var night = -5; night < 0; night++)
        {
            AddDocument(8_000);
            (await NightAsync(night)).SecondaryCopied.ShouldBeFalse();
        }

        Directory.Move(away, usb);
        _options.Backup.SecondaryDirectory = usb;
        AddDocument(8_000);
        var back = await NightAsync(0);

        back.SecondaryCopied.ShouldBeTrue();
        back.Warning.ShouldBeNull();
        Directory.EnumerateFiles(usb, "*.hc360bak").Select(Path.GetFileName).Order().ShouldBe(new[] { onDisk.FileName, back.FileName }.Order());
        BlobCount(usb).ShouldBe(_documents);
        foreach (var archive in new[] { onDisk, back })
        {
            var machine = Machine("restored-" + archive.FileName);
            var restored = await BackupRestore.RestoreAsync(Path.Combine(usb, archive.FileName), OriginalRecoveryKey(), Path.Combine(machine.Temp, "r.dump"),
                machine, new EphemeralDataProtectionProvider(), TimeProvider.System, Ct);
            restored.DocumentsRestored.ShouldBe(archive.DocumentCount!.Value);
        }
    }

    [Fact]
    public async Task Unused_documents_leave_both_folders_once_no_kept_archive_needs_them()
    {
        // F14: a document removed from the file store (erased, purged, moved to a new key) leaves the backups once the
        // last archive that lists it has expired.
        var kept = AddDocument(5_000);
        var erased = AddDocument(5_000);
        var erasedId = await BackupStore.HashFileAsync(Path.Combine(_directories.Files, erased), Ct);
        var old = await NightAsync(-40);
        File.Delete(Path.Combine(_directories.Files, erased));

        var tonight = await NightAsync(0);

        tonight.Warning.ShouldBeNull();
        foreach (var folder in new[] { _directories.Backups, Usb })
        {
            File.Exists(Path.Combine(folder, old.FileName)).ShouldBeFalse();
            File.Exists(Path.ChangeExtension(Path.Combine(folder, old.FileName), ".refs")).ShouldBeFalse();
            BlobIds(folder).ShouldNotContain(erasedId);
            BlobCount(folder).ShouldBe(1);
            (await ProblemsAsync(folder, tonight)).ShouldBeEmpty();
        }

        (await ContentsAsync(_directories.Backups, tonight)).Index.ShouldHaveSingleItem().RelativePath.ShouldBe(kept);
    }

    [Fact]
    public async Task A_document_moved_to_a_new_key_is_copied_again_and_each_backup_restores_its_own_bytes()
    {
        // F8: moving documents to a new key rewrites each one under the same name. The hash cache must not hand out the
        // earlier hash, or the second backup would restore the earlier bytes.
        var document = AddDocument(6_000);
        var path = Path.Combine(_directories.Files, document);
        var plain = _protector.UnprotectBytes(File.ReadAllBytes(path), "files");
        var before = File.ReadAllBytes(path);
        var first = await NightAsync(-1);

        var moved = path + ".moving";
        File.WriteAllBytes(moved, _protector.ProtectBytes(plain, "files"));
        File.Move(moved, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        var after = File.ReadAllBytes(path);
        after.Length.ShouldBe(before.Length);
        var second = await NightAsync(0);

        BlobCount(_directories.Backups).ShouldBe(2);
        (await RestoredBytesAsync(first, document)).ShouldBe(before);
        (await RestoredBytesAsync(second, document)).ShouldBe(after);
    }

    [Fact]
    public async Task After_a_new_recovery_key_earlier_backups_restore_and_unused_documents_still_go()
    {
        // F10: the server cannot open archives made with an earlier recovery key, yet their lists still decide what is kept.
        var document = AddDocument(4_000);
        var before = await NightAsync(-2);
        var admin = Guid.NewGuid();
        var created = _recoveryKeys.Create(admin);
        _recoveryKeys.Confirm(created.KeyId, created.Key, admin).IsSuccess.ShouldBeTrue();
        File.Delete(Path.Combine(_directories.Files, document));
        AddDocument(4_000);

        var after = await NightAsync(0);

        after.RecoveryKeyId.ShouldBe(created.KeyId);
        after.Warning.ShouldBeNull();
        BlobCount(_directories.Backups).ShouldBe(2, "the earlier archive is kept, and with it its document");
        var machine = Machine("restored");
        var restored = await BackupRestore.RestoreAsync(Path.Combine(Usb, before.FileName), OriginalRecoveryKey(), Path.Combine(machine.Temp, "r.dump"),
            machine, new EphemeralDataProtectionProvider(), TimeProvider.System, Ct);
        restored.DocumentsRestored.ShouldBe(1);
        File.Exists(Path.Combine(machine.Files, document)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_backup_before_an_update_lists_no_documents_and_copies_none()
    {
        AddDocuments(2, 3_000);
        var result = await Service().CreatePreMigrationAsync(["20260928102924_InitialCreate"], Ct);

        result.DocumentCount.ShouldBeNull();
        BlobCount(_directories.Backups).ShouldBe(0);
        (await ContentsAsync(_directories.Backups, result)).Manifest.IncludesDocuments.ShouldBeFalse();
        File.Exists(Path.ChangeExtension(Path.Combine(Usb, result.FileName), ".refs")).ShouldBeTrue();
    }

    [Fact]
    public async Task An_append_only_secondary_keeps_weekly_generations_and_each_archive_restores_from_it_alone()
    {
        // 5.6: an append-only or WORM share removes files by age, so each week's archives use a generation of their own.
        _options.Backup.SecondaryAppendOnly = true;
        var clock = new ManualTime(_now);
        AddDocuments(3, 5_000);
        var monday = await Service(clock).CreateAsync(BackupKind.Scheduled, clock.GetUtcNow(), includeDocuments: true, [], Ct);
        var firstGeneration = $"gen-{clock.GetUtcNow():yyyyMMdd}";

        clock.Advance(TimeSpan.FromDays(3));
        AddDocument(5_000);
        var thursday = await Service(clock).CreateAsync(BackupKind.Scheduled, clock.GetUtcNow(), includeDocuments: true, [], Ct);

        clock.Advance(TimeSpan.FromDays(5));
        AddDocument(5_000);
        var nextWeek = await Service(clock).CreateAsync(BackupKind.Scheduled, clock.GetUtcNow(), includeDocuments: true, [], Ct);
        var secondGeneration = $"gen-{clock.GetUtcNow():yyyyMMdd}";

        foreach (var result in new[] { monday, thursday, nextWeek })
        {
            result.SecondaryCopied.ShouldBeTrue();
            result.Warning.ShouldNotBeNull().ShouldStartWith(BackupService.OldCopiesKeptNote);
            BackupCopies.Reason(result.Warning).ShouldBeNull("an append-only share is not a failed copy");
        }

        BlobCount(Path.Combine(Usb, firstGeneration)).ShouldBe(4);
        BlobCount(Path.Combine(Usb, secondGeneration)).ShouldBe(5, "a new generation starts with every document again");
        Blobs(Usb).ShouldAllBe(f => File.GetLastWriteTimeUtc(f) != BackupStore.BlobTimestamp);
        foreach (var result in new[] { monday, thursday, nextWeek })
        {
            var machine = Machine("restored-" + result.FileName);
            var restored = await BackupRestore.RestoreAsync(Path.Combine(Usb, result.FileName), OriginalRecoveryKey(), Path.Combine(machine.Temp, "r.dump"),
                machine, new EphemeralDataProtectionProvider(), TimeProvider.System, Ct);
            restored.DocumentsRestored.ShouldBe(result.DocumentCount!.Value);
        }

        // The server's own folder is not append-only: one copy of each document there.
        BlobCount(_directories.Backups).ShouldBe(5);
    }

    private Task<BackupResult> NightAsync(int day) =>
        Service().CreateAsync(BackupKind.Scheduled, _now.AddDays(day), includeDocuments: true, ["20260928102924_InitialCreate"], Ct);

    private string AddDocument(int length)
    {
        var relative = $"2027/{(_documents % 12) + 1:00}/{Guid.NewGuid():N}.bin";
        var path = Path.Combine(_directories.Files, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, _protector.ProtectBytes(RandomNumberGenerator.GetBytes(length), "files"));
        _documents++;
        return relative;
    }

    private void AddDocuments(int count, int length)
    {
        for (var i = 0; i < count; i++)
        {
            AddDocument(length);
        }
    }

    private long DocumentBytes() => BackupArchive.ListDocuments(_directories.Files).Sum(d => d.Length);

    private static IEnumerable<string> Blobs(string folder) =>
        Directory.Exists(Path.Combine(folder, BackupStore.BlobsFolder))
            ? Directory.EnumerateFiles(Path.Combine(folder, BackupStore.BlobsFolder), "*" + BackupStore.BlobExtension, SearchOption.AllDirectories)
            : [];

    private static int BlobCount(string folder) => Blobs(folder).Count();

    private static long BlobBytes(string folder) => Blobs(folder).Sum(f => new FileInfo(f).Length);

    private static List<string> BlobIds(string folder) => Blobs(folder).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();

    private static long FolderBytes(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private byte[] OriginalRecoveryKey() => BackupRecoveryKey.Original(_protector.ExportMasterKey());

    private async Task<BackupContents> ContentsAsync(string folder, BackupResult result)
    {
        await using var input = File.OpenRead(Path.Combine(folder, result.FileName));
        await using var payload = await BackupEncryption.OpenAsync(input, OriginalRecoveryKey(), Ct);
        return await BackupArchive.ReadAsync(payload, new BackupReadTargets(), Ct);
    }

    private async Task<IReadOnlyList<string>> ProblemsAsync(string folder, BackupResult result) =>
        await BackupBlobs.CheckAsync(folder, await ContentsAsync(folder, result), Ct);

    private async Task<byte[]> RestoredBytesAsync(BackupResult result, string document)
    {
        var staging = Path.Combine(_root, "staging-" + result.FileName);
        await BackupBlobs.CopyAsync(_directories.Backups, await ContentsAsync(_directories.Backups, result), staging, Ct);
        return File.ReadAllBytes(Path.Combine(staging, document));
    }

    private DataDirectories Machine(string name)
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, name) });
        directories.EnsureCreated();
        return directories;
    }

    private BackupService Service(TimeProvider? time = null) => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _recoveryKeys,
        _protector,
        _sealKey,
        new NoDocumentsLock(),
        new FakePostgres(),
        Substitute.For<INotificationService>(),
        time ?? TimeProvider.System,
        NullLogger<BackupService>.Instance);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakePostgres : IPostgresTools
    {
        public string DatabaseName => "hc360_test";

        public Task<long> GetDatabaseSizeAsync(CancellationToken ct) => Task.FromResult(1024L);

        public Task DumpAsync(string outputFile, CancellationToken ct) =>
            File.WriteAllBytesAsync(outputFile, [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(4096)], ct);

        public Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct) => Task.FromResult(true);
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
