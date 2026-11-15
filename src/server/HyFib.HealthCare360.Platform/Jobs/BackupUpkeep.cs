using System.Globalization;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The backups' nightly upkeep (DED-16), on the server that runs the jobs, after the nightly backup
/// (<see cref="BackupOptions.UpkeepSchedule"/>, 03:30; <see cref="BackupUpkeepJob"/> runs its steps). A backup is its
/// archive and the documents it lists, which each backup folder keeps once (<see cref="BackupStore"/>), so every step
/// covers both: what a restore from that folder alone needs.
/// <list type="bullet">
/// <item>A USB disk that was unplugged, or the other of two disks that are swapped, comes back without the backups made
/// while it was away: each backup copies only itself. So the upkeep copies to the second disk or NAS the backups it lacks,
/// newest first and at most <see cref="MaxCatchUpCopies"/> a night, those retention would keep there
/// (<see cref="BackupRetention"/>): the documents each needs that the disk lacks, from the server's folder, each checked
/// against its SHA-256 as it is read, then its list, then the archive, checked against the SHA-256 its backup was verified
/// with as it is read and read back once written (<see cref="BackupStore.CopyArchiveAsync"/>). Then it removes there, and on
/// the server, what retention no longer keeps, as a backup does: that also happens on a night without a backup
/// (<see cref="CopyAsync"/>).</item>
/// <item>A copy written once may not read back years later, as a disk wears or a NAS's own disks fail. Each archive on the
/// second disk or NAS is read back and hashed every <see cref="BackupOptions.CopyCheckDays"/>, the least recently checked
/// first, for at most <see cref="CheckFor"/> a night, and every document it needs must be there. An archive that no longer
/// matches its backup is copied again from the server when the server's own still matches, and so is a document missing
/// there; otherwise the copy is flagged, on the backup and on System health (<see cref="CheckCopiesAsync"/>). And every copy
/// of a document there is read back once in <see cref="BackupOptions.CopyCheckDays"/> nights, a share of them a night: one
/// that no longer matches its SHA-256 is copied again from the server's folder, or, where the server has no good copy,
/// taken out of use and flagged on every backup there that needs it (<see cref="CheckDocumentsAsync"/>).</item>
/// </list>
/// </summary>
internal sealed class BackupUpkeep(
    IDbContextFactory<HospitalDbContext> contexts,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    IFieldKeyEscrow keys,
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
                PruneServer();
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
        var checkedDocuments = await CheckDocumentsAsync(secondary, runs, token => db.SaveChangesAsync(token), until, ct);
        if (checkedCopies > 0 || checkedDocuments > 0)
        {
            logger.LogInformation("Read back {Copies} backup copies and {Documents} document copies in {Directory}", checkedCopies, checkedDocuments, secondary);
        }
    }

    /// <summary>
    /// Reads back the copies in <paramref name="secondary"/> of <paramref name="runs"/> that are due (<see cref="BackupOptions.CopyCheckDays"/>),
    /// the least recently checked first, starting none after <paramref name="until"/>, and records on each run what was found,
    /// saved with <paramref name="save"/>: matched, mended from the server, or why it no longer matches. A copy matches when
    /// its archive hashes to the SHA-256 its backup was verified with and every document its list names is on the disk.
    /// </summary>
    /// <returns>How many copies were read back.</returns>
    internal async Task<int> CheckCopiesAsync(string secondary, IReadOnlyList<BackupRun> runs, Func<CancellationToken, Task> save, DateTimeOffset until,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var key = BackupService.ReferencesKeyOf(keys);
        try
        {
            BackupStore store;
            try
            {
                store = BackupStore.Open(secondary, keys.Fingerprint, key, time, Settings.SecondaryAppendOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "The backup copies in {Directory} could not be read back", secondary);
                return 0;
            }

            var disk = BackupDisk.Identify(secondary, now, logger);
            var every = TimeSpan.FromDays(Math.Max(Settings.CopyCheckDays, 1)) - NightlyMargin;
            var due = runs
                .Where(r => r is { FileName: not null, Sha256: not null } && File.Exists(Path.Combine(secondary, r.FileName)))
                .Select(r => (Run: r, Copy: BackupCopies.Read(r.SecondaryCopies).FirstOrDefault(c => c.Disk == disk)))
                .Where(d => d.Copy?.CheckedAt is not { } checkedAt || now - checkedAt >= every)
                .OrderBy(d => d.Copy?.CheckedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(d => d.Run.StartedAt)
                .ToList();

            // A document found on the disk tonight is not looked for again for the next backup that needs it.
            var present = new HashSet<string>(StringComparer.Ordinal);
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
                    problem = await CheckCopyAsync(store, path, run, ct) ?? await CheckDocumentsThereAsync(store, run, key, present, ct);
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
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Reads an archive's copy back: null while it matches its backup's SHA-256, or once copied again from the server's own
    /// file, which must match it first; otherwise why it does not match.
    /// </summary>
    private async Task<string?> CheckCopyAsync(BackupStore store, string path, BackupRun run, CancellationToken ct)
    {
        if (string.Equals(await BackupStore.HashFileAsync(path, ct), run.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var own = Path.Combine(directories.Backups, run.FileName!);
        if (!File.Exists(own) || !string.Equals(await BackupStore.HashFileAsync(own, ct), run.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("The copy {Path} does not match its backup, and the server has no good copy to replace it with", path);
            return "It no longer matches the SHA-256 the backup was verified with, and the server has no copy that does to replace it: the disk may be failing.";
        }

        try
        {
            // Held, so no backup's retention or copy changes the folder meanwhile.
            using (store.Lock())
            {
                File.Copy(own, path, overwrite: true);
            }

            if (string.Equals(await BackupStore.HashFileAsync(path, ct), run.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("The copy {Path} no longer matched its backup, and was copied again from the server", path);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A share that refuses to overwrite (DEPLOYMENT.md section 8) keeps the damaged copy, as does a folder another
            // backup holds, until the next night.
            logger.LogWarning(ex, "The copy {Path} no longer matches its backup and could not be replaced", path);
        }

        return "It no longer matches the SHA-256 the backup was verified with, and copying it again from the server did not mend it: the disk may be failing.";
    }

    /// <summary>
    /// The documents an archive on the disk needs, by its list there: each must have a copy on the disk, and one missing is
    /// copied again from the server's folder, checked against its SHA-256 as it is read. In an append-only folder, whose own
    /// retention removes old files, only a backup of the daily window is checked so: its documents must still be there
    /// (<see cref="BackupRetention.AppendOnlyMinimumDays"/>). Null when every document is there; otherwise why not.
    /// </summary>
    private async Task<string?> CheckDocumentsThereAsync(BackupStore store, BackupRun run, byte[] key, HashSet<string> present, CancellationToken ct)
    {
        if (store.AppendOnly && time.GetUtcNow() - run.StartedAt > TimeSpan.FromDays(Settings.Retention.DailyKept))
        {
            return null;
        }

        IReadOnlyList<string>? ids;
        try
        {
            ids = store.ReadReferences(run.FileName!, key);
        }
        catch (InvalidDataException ex)
        {
            return $"Its list of the documents it needs cannot be used, so they could not be checked: {ex.Message}";
        }

        if (ids is null)
        {
            return $"It has no list of the documents it needs beside it ({Path.ChangeExtension(run.FileName, BackupStore.ReferencesExtension)}), so they could not be checked.";
        }

        var missing = new List<string>();
        foreach (var id in ids.Where(id => !present.Contains(id)))
        {
            if (store.HasCopy(id))
            {
                present.Add(id);
            }
            else
            {
                missing.Add(id);
            }
        }

        if (missing.Count == 0)
        {
            return null;
        }

        var lost = await MendAsync(store, missing, key, ct);
        present.UnionWith(missing.Except(lost));
        if (lost.Count == 0)
        {
            logger.LogWarning("{Count} documents {File} needs were missing from {Directory}, and were copied again from the server", missing.Count, run.FileName, store.Directory);
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{lost.Count} of the documents it needs are missing from the disk, and the server has no good copy of them to put back, so it does not restore from this disk alone.");
    }

    /// <summary>
    /// Reads back tonight's share of the copies of documents on the disk (<see cref="BackupStore.CopiesInShare"/>), so each
    /// copy is read back once in <see cref="BackupOptions.CopyCheckDays"/> nights, starting none after <paramref name="until"/>.
    /// A copy that no longer matches its SHA-256 is taken out of use (<see cref="BackupStore.Discard"/>) and copied again from
    /// the server's folder, checked as it is read; where the server has no good copy, every backup on the disk that needs the
    /// document is flagged, saved with <paramref name="save"/>.
    /// </summary>
    /// <returns>How many copies were read back.</returns>
    internal async Task<int> CheckDocumentsAsync(string secondary, IReadOnlyList<BackupRun> runs, Func<CancellationToken, Task> save, DateTimeOffset until,
        CancellationToken ct)
    {
        var key = BackupService.ReferencesKeyOf(keys);
        try
        {
            BackupStore store;
            try
            {
                store = BackupStore.Open(secondary, keys.Fingerprint, key, time, Settings.SecondaryAppendOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "The document copies in {Directory} could not be read back", secondary);
                return 0;
            }

            var shares = Math.Max(Settings.CopyCheckDays, 1);
            var tonight = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime).DayNumber % shares;
            var damaged = new List<(string Id, string Path)>();
            var count = 0;
            try
            {
                foreach (var (id, path) in store.CopiesInShare(tonight, shares))
                {
                    ct.ThrowIfCancellationRequested();
                    if (time.GetUtcNow() >= until)
                    {
                        break;
                    }

                    string sha256;
                    try
                    {
                        sha256 = await BackupStore.HashFileAsync(path, ct);
                    }
                    catch (FileNotFoundException)
                    {
                        // Removed by retention meanwhile.
                        continue;
                    }

                    count++;
                    if (!string.Equals(sha256, id, StringComparison.Ordinal))
                    {
                        damaged.Add((id, path));
                    }
                }

                if (damaged.Count > 0)
                {
                    await MendDamagedAsync(store, damaged, runs, save, key, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Taken away, failing to read, or held by another backup: the next round of nights reads them again.
                logger.LogWarning(ex, "The document copies in {Directory} could not all be read back", secondary);
            }

            return count;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Takes the damaged copies out of use and puts back a good copy of each document from the server's folder; flags on
    /// <paramref name="runs"/> every backup on the disk that needs a document the server has no good copy of.
    /// </summary>
    private async Task MendDamagedAsync(BackupStore store, IReadOnlyList<(string Id, string Path)> damaged, IReadOnlyList<BackupRun> runs,
        Func<CancellationToken, Task> save, byte[] key, CancellationToken ct)
    {
        using (var held = store.Lock())
        {
            foreach (var (id, path) in damaged)
            {
                logger.LogWarning("The copy of a document in {Directory} ({Id}) no longer matches its SHA-256", store.Directory, id);
                store.Discard(held, path);
            }
        }

        var lost = await MendAsync(store, [.. damaged.Select(d => d.Id).Distinct(StringComparer.Ordinal).Where(id => !store.HasCopy(id))], key, ct);
        if (lost.Count == 0)
        {
            return;
        }

        logger.LogError("{Count} documents in {Directory} no longer read back as written, and the server has no good copy of them", lost.Count, store.Directory);
        var now = time.GetUtcNow();
        var disk = BackupDisk.Identify(store.Directory, now, logger);
        foreach (var run in runs.Where(r => r.FileName is not null && File.Exists(Path.Combine(store.Directory, r.FileName))))
        {
            IReadOnlyList<string>? ids;
            try
            {
                ids = store.ReadReferences(run.FileName!, key);
            }
            catch (InvalidDataException)
            {
                // Its own check says so (CheckCopiesAsync).
                continue;
            }

            var needed = ids?.Count(lost.Contains) ?? 0;
            if (needed == 0)
            {
                continue;
            }

            var copy = BackupCopies.Read(run.SecondaryCopies).FirstOrDefault(c => c.Disk == disk)
                       ?? new BackupCopy(disk, new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(store.Directory, run.FileName!)), TimeSpan.Zero));
            run.SecondaryCopies = BackupCopies.With(run.SecondaryCopies, copy with
            {
                CheckedAt = now,
                Problem = string.Create(CultureInfo.InvariantCulture,
                    $"{needed} of the documents it needs no longer read back as they were written, and the server has no good copy of them to put back: the disk may be failing."),
            });
        }

        await save(CancellationToken.None);
    }

    /// <summary>
    /// Copies each of <paramref name="ids"/> into <paramref name="store"/> from the server's backup folder, checked against its
    /// SHA-256 as it is read. Returns those the server has no good copy of.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> MendAsync(BackupStore store, IReadOnlyCollection<string> ids, byte[] key, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var lost = new List<string>();
        var server = BackupStore.Open(directories.Backups, keys.Fingerprint, key, time);
        using var held = store.Lock();
        foreach (var id in ids)
        {
            var source = server.PathOf(id);
            try
            {
                if (File.Exists(source))
                {
                    await store.AddAsync(held, source, id, ct);
                    continue;
                }
            }
            catch (InvalidDataException)
            {
                // The server's copy is damaged too.
            }

            lost.Add(id);
        }

        return lost;
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

        var key = BackupService.ReferencesKeyOf(keys);
        BackupStoreLock? held = null;
        try
        {
            BackupStore server;
            BackupStore target;
            try
            {
                server = BackupStore.Open(directories.Backups, keys.Fingerprint, key, time);
                target = BackupStore.Open(secondary, keys.Fingerprint, key, time, Settings.SecondaryAppendOnly);
                held = target.Lock();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "The backups {Directory} missed could not be copied there", secondary);
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

            // Only what retention keeps there, once copied: what it would remove at once is not copied, night after night. An
            // append-only share keeps each file for its own retention, at least the daily backups' days, so only the daily
            // backups are copied there.
            var retention = target.AppendOnly ? new BackupRetentionOptions { Daily = Settings.Retention.Daily, Weekly = 0, Monthly = 0 } : Settings.Retention;
            var removed = BackupRetention.ToRemove([.. present, .. missing.Select(m => m.File! with { Path = Path.Combine(secondary, Path.GetFileName(m.File.Path)) })],
                retention, Zone, now).Select(f => Path.GetFileName(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var due = missing
                .Where(m => !removed.Contains(m.Run.FileName!))
                .OrderByDescending(m => m.Run.StartedAt)
                .Take(MaxCatchUpCopies)
                .ToList();

            var copied = 0;
            foreach (var (run, file) in due)
            {
                ct.ThrowIfCancellationRequested();
                string? problem;
                try
                {
                    problem = server.ReadReferences(run.FileName!, key) is { } ids
                        ? await target.CopyArchiveAsync(held, server, file!.Path, run.Sha256!, ids, key, readBack: true, checkpoint: null, ct)
                        : $"it has no list of the documents it needs beside it on the server ({Path.ChangeExtension(run.FileName, BackupStore.ReferencesExtension)}).";
                }
                catch (InvalidDataException ex)
                {
                    // Its list does not verify, or a document it needs is damaged on the server's disk: not spread to the second.
                    problem = ex.Message;
                }
                catch (FileNotFoundException ex)
                {
                    problem = $"a document it needs is missing from the server's backup folder ({Path.GetFileName(ex.FileName)}).";
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
                logger.LogInformation("Backup {File}, missed while {Directory} was away, was copied there with its documents and checked", run.FileName, secondary);
            }

            if (!target.AppendOnly)
            {
                try
                {
                    Prune(target, held, key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A share that refuses deletes keeps them for its own retention (DEPLOYMENT.md section 8).
                    logger.LogInformation("Older backup copies in {Directory} were not removed: {Reason}", secondary, ex.Message);
                }
            }

            return copied;
        }
        finally
        {
            held?.Dispose();
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>A backup's note once its copy is made late: the failed copy's reason gone, any other note kept.</summary>
    internal static string CopiedLater(string? note, DateTimeOffset at, TimeZoneInfo zone)
    {
        var failed = note?.IndexOf(BackupService.CopyFailedNote, StringComparison.Ordinal) ?? -1;
        var kept = (failed >= 0 ? note![..failed] : note)?.Trim();
        var copied = BackupService.CopiedLaterNote + TimeZoneInfo.ConvertTime(at, zone).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + ".";
        return kept is { Length: > 0 } ? $"{kept} {copied}" : copied;
    }

    /// <summary>Retention on the server's own backup folder, on a night without a backup too, as after a backup.</summary>
    private void PruneServer()
    {
        var key = BackupService.ReferencesKeyOf(keys);
        try
        {
            var store = BackupStore.Open(directories.Backups, keys.Fingerprint, key, time);
            using var held = store.Lock();
            Prune(store, held, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The backup is made all the same; the next night tries again.
            logger.LogWarning(ex, "Backups that retention no longer keeps could not all be removed from {Directory}", directories.Backups);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The archives retention no longer keeps in <paramref name="store"/>, and the documents no remaining one needs.</summary>
    private void Prune(BackupStore store, BackupStoreLock held, byte[] key)
    {
        if (store.Prune(held, Settings.Retention, Zone, key).SweepSkipped is { } skipped)
        {
            logger.LogWarning("Unused documents were not removed from {Directory}: {Reason}", store.Directory, skipped);
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
