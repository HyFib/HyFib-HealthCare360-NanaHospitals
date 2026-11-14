using System.Globalization;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The backups' nightly upkeep (DED-16), on the server that runs the jobs, after the nightly backup
/// (<see cref="BackupOptions.UpkeepSchedule"/>, 03:30; <see cref="BackupUpkeepJob"/> runs its steps).
/// <list type="bullet">
/// <item>A USB disk that was unplugged, or the other of two disks that are swapped, comes back without the backups made
/// while it was away: each backup copies only itself. So the upkeep copies to the second disk or NAS the backups it lacks,
/// newest first and at most <see cref="MaxCatchUpCopies"/> a night, those retention would keep there
/// (<see cref="BackupRetention"/>), each checked against the SHA-256 its backup was verified with as it is read, and read
/// back once written. Then it removes there, and on the server, what retention no longer keeps, as a backup does: that
/// also happens on a night without a backup (<see cref="CopyAsync"/>).</item>
/// <item>A copy written once may not read back years later, as a disk wears or a NAS's own disks fail: each copy on the
/// second disk or NAS is read back and hashed every <see cref="BackupOptions.CopyCheckDays"/>, the least recently checked
/// first, for at most <see cref="CheckFor"/> a night. One that no longer matches its backup is copied again from the
/// server when the server's own still matches; otherwise it is flagged, on the backup and on System health
/// (<see cref="CheckCopiesAsync"/>).</item>
/// </list>
/// </summary>
internal sealed class BackupUpkeep(
    IDbContextFactory<HospitalDbContext> contexts,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    TimeProvider time,
    ILogger<BackupUpkeep> logger)
{
    /// <summary>The most backups copied to a disk that is back in one night: a week's, as a disk away for the weekly swap missed.</summary>
    public const int MaxCatchUpCopies = 7;

    /// <summary>How long after the upkeep starts the copies' checks may start another: the night's quiet hours, 03:30 to 06:30.</summary>
    public static readonly TimeSpan CheckFor = TimeSpan.FromHours(3);

    /// <summary>
    /// How much earlier than <see cref="BackupOptions.CopyCheckDays"/> a copy is due again: the upkeep runs once a night, so a
    /// copy checked at 03:40 is due again at 03:30 a week later, not eight nights on.
    /// </summary>
    private static readonly TimeSpan NightlyMargin = TimeSpan.FromHours(12);

    private BackupOptions Settings => options.Value.Backup;

    private TimeZoneInfo Zone => PlatformServiceCollectionExtensions.ResolveTimeZone(options.Value.TimeZone);

    /// <summary>The copies the second disk or NAS missed, then retention on both copies; one backup at a time, as a backup's own copy.</summary>
    public async Task CopyAsync(CancellationToken ct)
    {
        await BackupService.Gate.WaitAsync(ct);
        try
        {
            if (Settings.SecondaryDirectory is { Length: > 0 } secondary && Directory.Exists(directories.Backups))
            {
                await using var db = await contexts.CreateDbContextAsync(ct);
                var names = BackupFile.In(directories.Backups).Select(f => Path.GetFileName(f.Path)).ToList();
                var runs = await db.Set<BackupRun>()
                    .Where(r => r.Status == BackupStatus.Succeeded && r.FileName != null && names.Contains(r.FileName))
                    .ToListAsync(ct);
                await CatchUpCopiesAsync(secondary, runs, token => db.SaveChangesAsync(token), ct);
            }

            if (Directory.Exists(directories.Backups))
            {
                RemoveOld(directories.Backups);
            }
        }
        finally
        {
            BackupService.Gate.Release();
        }
    }

    /// <summary>The copies on the second disk or NAS read back, as the class says, until <paramref name="until"/>.</summary>
    public async Task CheckAsync(DateTimeOffset until, CancellationToken ct)
    {
        if (Settings.SecondaryDirectory is not { Length: > 0 } secondary || !Directory.Exists(secondary))
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var names = BackupFile.In(secondary).Select(f => Path.GetFileName(f.Path)).ToList();
        var runs = await db.Set<BackupRun>()
            .Where(r => r.Status == BackupStatus.Succeeded && r.FileName != null && names.Contains(r.FileName))
            .ToListAsync(ct);
        var checkedCopies = await CheckCopiesAsync(secondary, runs, token => db.SaveChangesAsync(token), until, ct);
        if (checkedCopies > 0)
        {
            logger.LogInformation("Read back {Count} backup copies in {Directory}", checkedCopies, secondary);
        }
    }

    /// <summary>
    /// Reads back the copies in <paramref name="secondary"/> of <paramref name="runs"/> that are due (<see cref="BackupOptions.CopyCheckDays"/>),
    /// the least recently checked first, starting none after <paramref name="until"/>, and records on each run what was found,
    /// saved with <paramref name="save"/>: matched, copied again from the server, or why it no longer matches.
    /// </summary>
    /// <returns>How many copies were read back.</returns>
    internal async Task<int> CheckCopiesAsync(string secondary, IReadOnlyList<BackupRun> runs, Func<CancellationToken, Task> save, DateTimeOffset until,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var disk = BackupDisk.Identify(secondary, now, logger);
        var every = TimeSpan.FromDays(Math.Max(Settings.CopyCheckDays, 1)) - NightlyMargin;
        var due = runs
            .Where(r => r is { FileName: not null, Sha256: not null } && File.Exists(Path.Combine(secondary, r.FileName)))
            .Select(r => (Run: r, Copy: BackupCopies.Read(r.SecondaryCopies).FirstOrDefault(c => c.Disk == disk)))
            .Where(d => d.Copy?.CheckedAt is not { } checkedAt || now - checkedAt >= every)
            .OrderBy(d => d.Copy?.CheckedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(d => d.Run.StartedAt)
            .ToList();

        var count = 0;
        foreach (var (run, copy) in due)
        {
            ct.ThrowIfCancellationRequested();
            if (time.GetUtcNow() >= until)
            {
                break;
            }

            var path = Path.Combine(secondary, run.FileName!);
            string? problem;
            try
            {
                problem = await CheckCopyAsync(path, run, ct);
            }
            catch (FileNotFoundException)
            {
                // Removed by retention meanwhile.
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Taken away, or failing to read at all: the next night tries again.
                logger.LogWarning(ex, "The copy {Path} could not be read back", path);
                break;
            }

            var checkedAt = time.GetUtcNow();
            run.SecondaryCopies = BackupCopies.With(run.SecondaryCopies,
                (copy ?? new BackupCopy(disk, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero))) with { CheckedAt = checkedAt, Problem = problem });
            await save(CancellationToken.None);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Reads a copy back: null while it matches its backup's SHA-256, or once copied again from the server's own file,
    /// which must match it first; otherwise why it does not match.
    /// </summary>
    private async Task<string?> CheckCopyAsync(string path, BackupRun run, CancellationToken ct)
    {
        if (string.Equals(await Sha256Async(path, ct), run.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var own = Path.Combine(directories.Backups, run.FileName!);
        if (!File.Exists(own) || !string.Equals(await Sha256Async(own, ct), run.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("The copy {Path} does not match its backup, and the server has no good copy to replace it with", path);
            return "It no longer matches the SHA-256 the backup was verified with, and the server has no copy that does to replace it: the disk may be failing.";
        }

        try
        {
            File.Copy(own, path, overwrite: true);
            if (string.Equals(await Sha256Async(path, ct), run.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("The copy {Path} no longer matched its backup, and was copied again from the server", path);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A share that refuses to overwrite (DEPLOYMENT.md section 8) keeps the damaged copy.
            logger.LogWarning(ex, "The copy {Path} no longer matches its backup and could not be replaced", path);
        }

        return "It no longer matches the SHA-256 the backup was verified with, and copying it again from the server did not mend it: the disk may be failing.";
    }

    /// <summary>
    /// Copies to <paramref name="secondary"/> the backups of <paramref name="runs"/> it lacks, as the class says, recording
    /// each on its run with <paramref name="save"/>; nothing when the disk or share is not there.
    /// </summary>
    /// <returns>How many backups were copied.</returns>
    internal async Task<int> CatchUpCopiesAsync(string secondary, IReadOnlyList<BackupRun> runs, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(secondary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still away: System health says so of the newest backup's copy (BackupCopies).
            logger.LogDebug(ex, "The secondary backup directory {Directory} is not there", secondary);
            return 0;
        }

        var now = time.GetUtcNow();
        var present = BackupFile.In(secondary);
        string? disk = null;
        var missing = runs
            .Where(r => r is { Status: BackupStatus.Succeeded, FileName: not null, Sha256: not null } && !File.Exists(Path.Combine(secondary, r.FileName)))
            .Select(r => (Run: r, File: BackupFile.From(Path.Combine(directories.Backups, r.FileName!))))
            .Where(m => m.File is not null && File.Exists(m.File.Path))
            .ToList();

        // Only what retention keeps there, once copied: what it would remove at once is not copied, night after night.
        var removed = BackupRetention.ToRemove([.. present, .. missing.Select(m => m.File! with { Path = Path.Combine(secondary, Path.GetFileName(m.File.Path)) })],
            Settings.Retention, Zone, now).Select(f => Path.GetFileName(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var due = missing
            .Where(m => !removed.Contains(m.Run.FileName!))
            .OrderByDescending(m => m.Run.StartedAt)
            .Take(MaxCatchUpCopies)
            .ToList();

        var copied = 0;
        foreach (var (run, file) in due)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(secondary, run.FileName!);
            string? problem;
            try
            {
                problem = await CopyCheckedAsync(file!.Path, target, run.Sha256!, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Taken away again in the middle, or full: the next night goes on.
                logger.LogWarning(ex, "Backup {File} could not be copied to {Directory}, which was back", run.FileName, secondary);
                break;
            }

            if (problem is not null)
            {
                logger.LogError("Backup {File} was not copied to {Directory}: {Problem}", run.FileName, secondary, problem);
                continue;
            }

            disk ??= BackupDisk.Identify(secondary, now, logger);
            run.SecondaryCopied = true;
            run.SecondaryCopies = BackupCopies.With(run.SecondaryCopies, new BackupCopy(disk, time.GetUtcNow()));
            run.Error = CopiedLater(run.Error, now, Zone);
            await save(CancellationToken.None);
            copied++;
            logger.LogInformation("Backup {File}, missed while {Directory} was away, was copied there and checked", run.FileName, secondary);
        }

        try
        {
            RemoveOld(secondary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An append-only share keeps them for its own retention (DEPLOYMENT.md section 8).
            logger.LogInformation("Older backup copies in {Directory} were not removed: {Reason}", secondary, ex.Message);
        }

        return copied;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/>, which must not exist: the SHA-256 of what is read must
    /// be <paramref name="sha256"/>, the one the backup was verified with, so a backup damaged on the server's disk is not
    /// spread to the second; and the copy, read back, the same, so one the disk did not write as sent is not counted.
    /// </summary>
    /// <returns>Why it was not copied, the copy removed; null once it is copied and checked.</returns>
    internal static async Task<string?> CopyCheckedAsync(string source, string target, string sha256, CancellationToken ct)
    {
        string read;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }

            output.Flush(flushToDisk: true);
            read = Convert.ToHexString(hash.GetHashAndReset());
        }

        var problem = !string.Equals(read, sha256, StringComparison.OrdinalIgnoreCase)
            ? $"{Path.GetFileName(source)} on the server no longer matches the SHA-256 it was checked with when it was made: the server's disk may be failing."
            : !string.Equals(await Sha256Async(target, ct), sha256, StringComparison.OrdinalIgnoreCase)
                ? $"The copy of {Path.GetFileName(source)} does not read back as it was written: the disk may be failing."
                : null;
        if (problem is not null)
        {
            TryDelete(target);
        }

        return problem;
    }

    /// <summary>The SHA-256 of <paramref name="path"/>, in upper-case hex as backups record it.</summary>
    internal static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>A backup's note once its copy is made late: the failed copy's reason gone, any other note kept.</summary>
    internal static string CopiedLater(string? note, DateTimeOffset at, TimeZoneInfo zone)
    {
        var failed = note?.IndexOf(BackupService.CopyFailedNote, StringComparison.Ordinal) ?? -1;
        var kept = (failed >= 0 ? note![..failed] : note)?.Trim();
        var copied = BackupService.CopiedLaterNote + TimeZoneInfo.ConvertTime(at, zone).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + ".";
        return kept is { Length: > 0 } ? $"{kept} {copied}" : copied;
    }

    private void RemoveOld(string directory) => BackupRetention.Apply(directory, Settings.Retention, Zone, time.GetUtcNow(), logger);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An append-only share keeps it; read back, it does not match, and is never counted as a copy.
        }
    }
}

/// <summary>
/// Runs the backups' nightly upkeep at <see cref="BackupOptions.UpkeepSchedule"/>, each step whatever became of the one
/// before: the copies the second disk missed and retention (<see cref="BackupUpkeep.CopyAsync"/>), the test restore when
/// one is due (<see cref="BackupRestoreTest"/>), then the copies read back with the time left (<see cref="BackupUpkeep.CheckAsync"/>).
/// </summary>
[DisallowConcurrentExecution]
internal sealed class BackupUpkeepJob(BackupUpkeep upkeep, BackupRestoreTest restoreTest, TimeProvider time, ILogger<BackupUpkeepJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + BackupUpkeep.CheckFor;
        await StepAsync("copying the backups the second disk missed", () => upkeep.CopyAsync(cancellationToken));
        await StepAsync("the test restore", () => restoreTest.RunAsync(force: false, cancellationToken));
        await StepAsync("reading back the copies on the second disk", () => upkeep.CheckAsync(until, cancellationToken));
    }

    private async Task StepAsync(string step, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The backups' nightly upkeep failed at {Step}", step);
        }
    }
}
