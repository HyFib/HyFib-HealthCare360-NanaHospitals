using System.Globalization;
using System.Security.Cryptography;
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

/// <summary>
/// The backups' nightly upkeep (DED-16) on a hospital that swaps two USB disks weekly: the disk that comes back gets the
/// backups made while it was away, newest first and a week's at most a night, each checked as it is read and once
/// written, so the copy off the server's own disk is whole again by morning rather than only from the next backup on.
/// </summary>
public sealed class BackupUpkeepTests : IDisposable
{
    private const string Unplugged = BackupService.CopyFailedNote + "Could not find a part of the path.";

    // 03:30 IST, after the night's backup.
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 3, 30, 0, TimeSpan.FromHours(5.5));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly string _disk;
    private readonly FieldProtector _protector;
    private readonly RecoveryKeyStore _recoveryKeys;
    private readonly Auditing.AuditSealKey _sealKey;

    public BackupUpkeepTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _disk = Path.Combine(_root, "usb", "HC360-Backups");
        _options.Backup.SecondaryDirectory = _disk;
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        Directory.CreateDirectory(_directories.Backups);
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
    /// The disk was taken out after the night of the 7th and is back: the seven backups made since are copied, the newest
    /// first, each as made and read back from the disk; their notes no longer say the copy failed, and System health no
    /// longer warns of the newest (<see cref="BackupCopies"/>, which reads <see cref="BackupRun.SecondaryCopied"/>).
    /// </summary>
    [Fact]
    public async Task A_disk_back_after_a_week_away_gets_the_backups_it_missed_newest_first_each_checked()
    {
        var runs = Enumerable.Range(5, 10).Select(day => Backup(day, onDisk: day <= 7)).ToList();
        var saved = new List<string>();

        var copied = await Upkeep().CatchUpCopiesAsync(_disk, runs, _ => Saved(saved, runs), Ct);

        copied.ShouldBe(7);
        saved.ShouldBe([.. Enumerable.Range(8, 7).Reverse().Select(day => runs[day - 5].FileName!)], "the newest first");
        foreach (var run in runs)
        {
            File.ReadAllBytes(Path.Combine(_disk, run.FileName!)).ShouldBe(File.ReadAllBytes(Path.Combine(_directories.Backups, run.FileName!)));
            run.SecondaryCopied.ShouldBeTrue();
        }

        runs[^1].Error.ShouldBe("Copied to the secondary directory later, once it was back: 14 Nov 2026 03:30.");
        BackupCopies.Reason(runs[^1].Error).ShouldBeNull("no longer a failed copy");
        BackupCopies.Read(runs[^1].SecondaryCopies).ShouldHaveSingleItem().Disk.ShouldBe(BackupDisk.Identify(_disk, Now, NullLogger.Instance), "the disk it went to");
        Directory.EnumerateFiles(_disk, "*.partial").ShouldBeEmpty();
    }

    /// <summary>A disk away for longer gets a week's backups a night, the newest first, and the rest the nights after.</summary>
    [Fact]
    public async Task At_most_a_week_of_backups_is_copied_in_a_night()
    {
        var runs = Enumerable.Range(1, 10).Select(day => Backup(day, onDisk: false)).ToList();

        (await Upkeep().CatchUpCopiesAsync(_disk, runs, _ => Task.CompletedTask, Ct)).ShouldBe(BackupUpkeep.MaxCatchUpCopies);
        runs.Where(r => !r.SecondaryCopied).Select(r => r.StartedAt).ShouldBe([.. runs.Take(3).Select(r => r.StartedAt)], "the oldest wait");

        (await Upkeep().CatchUpCopiesAsync(_disk, runs, _ => Task.CompletedTask, Ct)).ShouldBe(3);
        runs.ShouldAllBe(r => r.SecondaryCopied);
    }

    /// <summary>
    /// A backup whose file on the server no longer matches the SHA-256 it was verified with (the server's disk failing) is
    /// not spread to the second disk: no copy is left of it there, and the others are copied.
    /// </summary>
    [Fact]
    public async Task A_backup_damaged_on_the_server_is_not_spread_to_the_disk()
    {
        var runs = Enumerable.Range(10, 3).Select(day => Backup(day, onDisk: false)).ToList();
        var damaged = Path.Combine(_directories.Backups, runs[1].FileName!);
        var bytes = File.ReadAllBytes(damaged);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(damaged, bytes);

        (await Upkeep().CatchUpCopiesAsync(_disk, runs, _ => Task.CompletedTask, Ct)).ShouldBe(2);

        File.Exists(Path.Combine(_disk, runs[1].FileName!)).ShouldBeFalse();
        (runs[0].SecondaryCopied, runs[1].SecondaryCopied, runs[2].SecondaryCopied).ShouldBe((true, false, true));
        runs[1].Error.ShouldBe(Unplugged, "its note still says its copy failed");
    }

    [Fact]
    public async Task Nothing_is_copied_while_the_disk_is_away()
    {
        var runs = Enumerable.Range(10, 3).Select(day => Backup(day, onDisk: false)).ToList();
        var unplugged = Path.Combine(_root, "unplugged");
        await File.WriteAllTextAsync(unplugged, string.Empty, Ct);

        (await Upkeep().CatchUpCopiesAsync(Path.Combine(unplugged, "HC360-Backups"), runs, _ => Task.CompletedTask, Ct)).ShouldBe(0);

        runs.ShouldAllBe(r => !r.SecondaryCopied);
    }

    /// <summary>
    /// Only what retention keeps on the disk once copied: a backup it would remove there at once is not copied, night after
    /// night. Here three days are kept, the disk has the 12th and 13th, and the server also the 10th and the 14th.
    /// </summary>
    [Fact]
    public async Task A_backup_retention_would_remove_from_the_disk_is_not_copied()
    {
        _options.Backup.Retention = new BackupRetentionOptions { Daily = 3, Weekly = 0, Monthly = 0 };
        var runs = new[] { Backup(10, onDisk: false), Backup(12, onDisk: true), Backup(13, onDisk: true), Backup(14, onDisk: false) };

        (await Upkeep().CatchUpCopiesAsync(_disk, runs, _ => Task.CompletedTask, Ct)).ShouldBe(1);

        (runs[0].SecondaryCopied, runs[3].SecondaryCopied).ShouldBe((false, true));
        File.Exists(Path.Combine(_disk, runs[0].FileName!)).ShouldBeFalse();
    }

    /// <summary>Once the copies are made, retention runs on the disk too: what it no longer keeps there goes.</summary>
    [Fact]
    public async Task Retention_runs_on_the_disk_after_the_copies()
    {
        _options.Backup.Retention = new BackupRetentionOptions { Daily = 3, Weekly = 0, Monthly = 0 };
        var runs = new[] { Backup(9, onDisk: true), Backup(12, onDisk: true), Backup(13, onDisk: true), Backup(14, onDisk: false) };

        await Upkeep().CatchUpCopiesAsync(_disk, runs, _ => Task.CompletedTask, Ct);

        Directory.EnumerateFiles(_disk, "*.hc360bak").Select(Path.GetFileName).ShouldBe(runs.Skip(1).Select(r => r.FileName), ignoreOrder: true);
    }

    /// <summary>
    /// Each copy on the disk is read back and hashed: those that match are recorded as checked; one that no longer matches
    /// its backup (the disk wearing) is copied again from the server, whose own still matches, and is then whole.
    /// </summary>
    [Fact]
    public async Task Copies_on_the_disk_are_read_back_and_a_damaged_one_mended_from_the_server()
    {
        var runs = Enumerable.Range(10, 3).Select(day => Backup(day, onDisk: true)).ToList();
        var damaged = Path.Combine(_disk, runs[1].FileName!);
        Damage(damaged);

        var checkedCopies = await Upkeep().CheckCopiesAsync(_disk, runs, _ => Task.CompletedTask, Now.AddHours(3), Ct);

        checkedCopies.ShouldBe(3);
        File.ReadAllBytes(damaged).ShouldBe(File.ReadAllBytes(Path.Combine(_directories.Backups, runs[1].FileName!)));
        var disk = BackupDisk.Identify(_disk, Now, NullLogger.Instance);
        foreach (var run in runs)
        {
            BackupCopies.Read(run.SecondaryCopies).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                copy => copy.Disk.ShouldBe(disk),
                copy => copy.CheckedAt.ShouldBe(Now),
                copy => copy.Problem.ShouldBeNull());
        }
    }

    /// <summary>A damaged copy the server can no longer mend (its own backup removed by retention) is flagged on its backup.</summary>
    [Fact]
    public async Task A_damaged_copy_the_server_cannot_mend_is_flagged_on_its_backup()
    {
        var runs = Enumerable.Range(10, 2).Select(day => Backup(day, onDisk: true)).ToList();
        File.Delete(Path.Combine(_directories.Backups, runs[0].FileName!));
        Damage(Path.Combine(_disk, runs[0].FileName!));

        await Upkeep().CheckCopiesAsync(_disk, runs, _ => Task.CompletedTask, Now.AddHours(3), Ct);

        BackupCopies.Read(runs[0].SecondaryCopies).ShouldHaveSingleItem().Problem.ShouldNotBeNull()
            .ShouldContain("the server has no copy that does to replace it: the disk may be failing");
        BackupCopies.Read(runs[1].SecondaryCopies).ShouldHaveSingleItem().Problem.ShouldBeNull();
    }

    /// <summary>A copy checked within the week is not read again; and none starts once the night's quiet hours are over.</summary>
    [Fact]
    public async Task Copies_checked_within_the_week_wait_and_none_starts_past_the_nights_end()
    {
        var runs = Enumerable.Range(10, 2).Select(day => Backup(day, onDisk: true)).ToList();
        var disk = BackupDisk.Identify(_disk, Now, NullLogger.Instance);
        runs[0].SecondaryCopies = BackupCopies.With(null, new BackupCopy(disk, runs[0].StartedAt, CheckedAt: Now.AddDays(-2)));

        (await Upkeep().CheckCopiesAsync(_disk, runs, _ => Task.CompletedTask, until: Now, Ct)).ShouldBe(0, "the night's checks are over");
        (await Upkeep().CheckCopiesAsync(_disk, runs, _ => Task.CompletedTask, Now.AddHours(3), Ct)).ShouldBe(1, "only the one never read back");
        BackupCopies.Read(runs[0].SecondaryCopies).ShouldHaveSingleItem().CheckedAt.ShouldBe(Now.AddDays(-2));
    }

    /// <summary>
    /// A backup is its archive and the documents it lists, kept once in each backup folder: the disk that was away gets,
    /// with each backup it missed, the documents it needs that the disk lacks (those added while it was away), so every
    /// backup on the disk restores from the disk alone.
    /// </summary>
    [Fact]
    public async Task A_disk_back_gets_the_documents_of_the_backups_it_missed_and_each_restores_from_it_alone()
    {
        AddDocument("2026/11/before.bin");
        var first = await BackUpAsync(-3);
        first.SecondaryCopied.ShouldBeTrue();
        Unplug();
        AddDocument("2026/11/while-away-1.bin");
        var second = await BackUpAsync(-2);
        AddDocument("2026/11/while-away-2.bin");
        var third = await BackUpAsync(-1);
        (second.SecondaryCopied, third.SecondaryCopied).ShouldBe((false, false));
        PlugIn();
        var runs = new[] { first, second, third }.Select(Run).ToList();

        (await Upkeep().CatchUpCopiesAsync(_disk, runs, _ => Task.CompletedTask, Ct)).ShouldBe(2);

        runs.ShouldAllBe(r => r.SecondaryCopied);
        foreach (var run in runs)
        {
            (await ProblemsAsync(_disk, run.FileName!)).ShouldBeEmpty($"{run.FileName} restores from the disk alone");
        }

        Directory.EnumerateFiles(_disk, "*.partial").ShouldBeEmpty();
    }

    /// <summary>
    /// A document a backup on the disk needs that is gone from the disk (deleted by hand, say) is copied there again from the
    /// server's folder when the copy is read back, and the copy is then whole.
    /// </summary>
    [Fact]
    public async Task A_document_missing_from_the_disk_is_copied_there_again_when_the_copy_is_read_back()
    {
        AddDocument("2026/11/a.bin");
        AddDocument("2026/11/b.bin");
        var runs = new[] { await BackUpAsync(-2), await BackUpAsync(-1) }.Select(Run).ToList();
        var gone = Blobs(_disk).First();
        var content = File.ReadAllBytes(Path.Combine(_directories.Backups, Path.GetRelativePath(_disk, gone)));
        File.Delete(gone);

        (await Upkeep().CheckCopiesAsync(_disk, runs, _ => Task.CompletedTask, Now.AddHours(3), Ct)).ShouldBe(2);

        File.ReadAllBytes(gone).ShouldBe(content, "copied again from the server");
        runs.ShouldAllBe(r => BackupCopies.Read(r.SecondaryCopies).Single().Problem == null);
        foreach (var run in runs)
        {
            (await ProblemsAsync(_disk, run.FileName!)).ShouldBeEmpty();
        }
    }

    /// <summary>
    /// The copies of documents on the disk are read back too, a share of them each night: one that no longer matches its
    /// SHA-256 is copied again from the server's folder; one the server has no good copy of either is taken out of use and
    /// flagged on every backup on the disk that needs it, which System health then names.
    /// </summary>
    [Fact]
    public async Task A_document_damaged_on_the_disk_is_mended_from_the_server_or_flagged_on_the_backups_that_need_it()
    {
        _options.Backup.CopyCheckDays = 1;
        AddDocument("2026/11/a.bin");
        AddDocument("2026/11/b.bin");
        var runs = new[] { await BackUpAsync(-2), await BackUpAsync(-1) }.Select(Run).ToList();
        var blobs = Blobs(_disk);
        blobs.Count.ShouldBe(2);
        var mended = blobs[0];
        var good = File.ReadAllBytes(mended);
        Damage(mended);
        var lost = blobs[1];
        Damage(lost);
        Damage(Path.Combine(_directories.Backups, Path.GetRelativePath(_disk, lost)));

        (await Upkeep().CheckDocumentsAsync(_disk, runs, _ => Task.CompletedTask, Now.AddHours(3), Ct)).ShouldBe(2, "every copy, with one share a night");

        File.ReadAllBytes(mended).ShouldBe(good, "copied again from the server, whose copy still matches");
        File.Exists(lost).ShouldBeFalse("taken out of use: it is no copy of the document");
        foreach (var run in runs)
        {
            var copy = BackupCopies.Read(run.SecondaryCopies).ShouldHaveSingleItem();
            copy.CheckedAt.ShouldBe(Now);
            copy.Problem.ShouldNotBeNull().ShouldContain("1 of the documents it needs no longer read back as they were written");
        }

        Networking.SystemHealthBackups.DamagedCopiesWarning(runs.SelectMany(r => BackupCopies.Read(r.SecondaryCopies).Select(c => (r.FileName!, c))), 1, Now)
            .ShouldNotBeNull().Code.ShouldBe(Networking.SystemHealthBackups.CopyDamagedCode);
    }

    [Fact]
    public void The_note_of_a_copy_made_late_keeps_any_other_note() =>
        BackupUpkeep.CopiedLater("Checked without pg_restore: the database dump was not listed. " + Unplugged, Now, HospitalTimeZone.Zone)
            .ShouldBe("Checked without pg_restore: the database dump was not listed. Copied to the secondary directory later, once it was back: 14 Nov 2026 03:30.");

    /// <summary>
    /// The nightly backup of <paramref name="day"/> November at 01:30 IST, on the server and, when <paramref name="onDisk"/>,
    /// copied to the disk then; otherwise its copy failed, the disk being away.
    /// </summary>
    private BackupRun Backup(int day, bool onDisk)
    {
        var startedAt = new DateTimeOffset(2026, 11, day, 1, 30, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
        var fileName = string.Create(CultureInfo.InvariantCulture, $"hc360-{startedAt:yyyyMMdd-HHmmss}-scheduled.hc360bak");
        var content = RandomNumberGenerator.GetBytes(3000 + day);
        File.WriteAllBytes(Path.Combine(_directories.Backups, fileName), content);
        if (onDisk)
        {
            Directory.CreateDirectory(_disk);
            File.WriteAllBytes(Path.Combine(_disk, fileName), content);
        }

        return new BackupRun
        {
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(10),
            Kind = BackupKind.Scheduled,
            Status = BackupStatus.Succeeded,
            FileName = fileName,
            SizeBytes = content.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)),
            VerifiedAt = startedAt.AddMinutes(9),
            SecondaryCopied = onDisk,
            Error = onDisk ? null : Unplugged,
        };
    }

    private static void Damage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[^10] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static Task Saved(List<string> saved, IReadOnlyList<BackupRun> runs)
    {
        saved.AddRange(runs.Where(r => r.SecondaryCopied && !saved.Contains(r.FileName!) && r.Error is not null).Select(r => r.FileName!));
        return Task.CompletedTask;
    }

    private BackupUpkeep Upkeep() => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _protector,
        new FixedClock(Now),
        NullLogger<BackupUpkeep>.Instance);

    private void AddDocument(string relativePath)
    {
        var path = Path.Combine(_directories.Files, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, _protector.ProtectBytes(RandomNumberGenerator.GetBytes(2_000), "files"));
    }

    /// <summary>A real backup run, <paramref name="day"/> days from <see cref="Now"/>, copied to the disk when it is plugged in.</summary>
    private Task<BackupResult> BackUpAsync(int day) =>
        new BackupService(Substitute.For<IDbContextFactory<HospitalDbContext>>(), Options.Create(_options), _directories, _recoveryKeys, _protector, _sealKey,
                new NoDocumentsLock(), new FakePostgres(), Substitute.For<INotificationService>(), new FixedClock(Now), NullLogger<BackupService>.Instance)
            .CreateAsync(BackupKind.Scheduled, Now.AddDays(day).ToUniversalTime(), includeDocuments: true, [], Ct);

    private static BackupRun Run(BackupResult result) => new()
    {
        StartedAt = result.StartedAt,
        CompletedAt = result.CompletedAt,
        Kind = result.Kind,
        Status = BackupStatus.Succeeded,
        FileName = result.FileName,
        SizeBytes = result.SizeBytes,
        Sha256 = result.Sha256,
        VerifiedAt = result.VerifiedAt,
        SecondaryCopied = result.SecondaryCopied,
        SecondaryCopies = result.SecondaryCopied ? BackupCopies.With(null, new BackupCopy(result.SecondaryDisk, result.CompletedAt)) : null,
        Error = result.Warning,
        RecoveryKeyId = result.RecoveryKeyId,
    };

    private void Unplug()
    {
        var unplugged = Path.Combine(_root, "unplugged");
        File.WriteAllText(unplugged, string.Empty);
        _options.Backup.SecondaryDirectory = Path.Combine(unplugged, "HC360-Backups");
    }

    private void PlugIn() => _options.Backup.SecondaryDirectory = _disk;

    private static List<string> Blobs(string folder) =>
        [.. Directory.EnumerateFiles(Path.Combine(folder, BackupStore.BlobsFolder), "*" + BackupStore.BlobExtension, SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    /// <summary>What a restore of the backup from <paramref name="folder"/> alone would find wrong with its documents; empty when nothing.</summary>
    private async Task<IReadOnlyList<string>> ProblemsAsync(string folder, string fileName)
    {
        await using var input = File.OpenRead(Path.Combine(folder, fileName));
        await using var payload = await BackupEncryption.OpenAsync(input, BackupRecoveryKey.Original(_protector.ExportMasterKey()), Ct);
        return await BackupBlobs.CheckAsync(folder, await BackupArchive.ReadAsync(payload, new BackupReadTargets(), Ct), Ct);
    }

    private sealed class FakePostgres : IPostgresTools
    {
        public string DatabaseName => "hc360_test";

        public Task<long> GetDatabaseSizeAsync(CancellationToken ct) => Task.FromResult(1024L);

        public async Task<IReadOnlyDictionary<string, long>> DumpAsync(string outputFile, bool lowPriority, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(outputFile, [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(512)], ct);
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
