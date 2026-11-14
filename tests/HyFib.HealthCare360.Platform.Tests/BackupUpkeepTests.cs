using System.Globalization;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
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

    public BackupUpkeepTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _disk = Path.Combine(_root, "usb", "HC360-Backups");
        _options.Backup.SecondaryDirectory = _disk;
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        Directory.CreateDirectory(_directories.Backups);
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

    private static Task Saved(List<string> saved, IReadOnlyList<BackupRun> runs)
    {
        saved.AddRange(runs.Where(r => r.SecondaryCopied && !saved.Contains(r.FileName!) && r.Error is not null).Select(r => r.FileName!));
        return Task.CompletedTask;
    }

    private BackupUpkeep Upkeep() => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        new FixedClock(Now),
        NullLogger<BackupUpkeep>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
