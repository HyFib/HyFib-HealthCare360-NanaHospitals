using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

public enum BackupKind
{
    Scheduled = 0,
    Manual,
    PreMigration,
}

public enum BackupStatus
{
    Running = 0,
    Succeeded,
    Failed,
}

public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BackupKind Kind { get; set; }
    public BackupStatus Status { get; set; }
    public string? FileName { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public bool SecondaryCopied { get; set; }
    public string? Error { get; set; }
    public Guid? TriggeredBy { get; set; }

    /// <summary>
    /// 1: the database only (backups made before archive backups existed). 2: an archive with the database, the field
    /// encryption key and, for full backups, the stored documents (<see cref="BackupArchive"/>). 3: the same, in a file
    /// that names its recovery key.
    /// </summary>
    public int FormatVersion { get; set; } = BackupEncryption.LegacyVersion;

    /// <summary>Documents in the backup; null when it holds the database only (older backups, and those taken before an update).</summary>
    public int? DocumentCount { get; set; }

    /// <summary>When the finished file was read back in full, checked against its manifest and its dump listed by pg_restore.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>
    /// The id of the recovery key that opens the backup (<see cref="BackupRecoveryKey.Id"/>). Null for backups made before
    /// backups named their key, which all open with the original recovery key.
    /// </summary>
    public string? RecoveryKeyId { get; set; }

    /// <summary>When its copy in the off-site bucket was read back and found to match (<see cref="BackupOffsiteUploader"/>); null until then.</summary>
    public DateTimeOffset? OffsiteUploadedAt { get; set; }

    /// <summary>The object key of its off-site copy, from its first upload attempt.</summary>
    public string? OffsiteKey { get; set; }

    /// <summary>The size of its off-site copy as the store reported it once uploaded.</summary>
    public long? OffsiteSizeBytes { get; set; }

    /// <summary>How many times its upload was tried, the one that succeeded included.</summary>
    public int OffsiteAttempts { get; set; }

    /// <summary>Why its last upload attempt failed; null once it is off site, and before any attempt.</summary>
    public string? OffsiteError { get; set; }

    /// <summary>
    /// The upload under way (JSON, <see cref="OffsiteUploadState"/>): the multipart upload's id and the parts the store has
    /// taken, so a large backup on a slow uplink goes on where it stopped, across failures and restarts. Null otherwise.
    /// </summary>
    public string? OffsiteUpload { get; set; }

    /// <summary>
    /// Its copies on the second disk or NAS (JSON, a list of <see cref="BackupCopy"/>): the disk each is on, by the id it
    /// was given on first use (<see cref="BackupDisk"/>), when it was made, and when it was last read back and whether it,
    /// and the documents it needs there, still matched. Two disks that are swapped each get their copies; null before any
    /// copy, and for backups made before disks were told apart.
    /// </summary>
    public string? SecondaryCopies { get; set; }

    /// <summary>When it was last restored into a scratch database to check it (<see cref="BackupRestoreTest"/>); null if never.</summary>
    public DateTimeOffset? RestoreTestedAt { get; set; }

    /// <summary>Whether that test restore passed: restored without error, its key tables holding the rows recorded when it was made.</summary>
    public bool? RestoreTestPassed { get; set; }

    /// <summary>What that test restore found: the rows it checked, or why it failed.</summary>
    public string? RestoreTestNote { get; set; }
}

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> b)
    {
        b.ToTable("backup_runs", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.Property(x => x.RecoveryKeyId).HasMaxLength(16);
        b.Property(x => x.OffsiteKey).HasMaxLength(1024);
        b.Property(x => x.OffsiteError).HasMaxLength(2000);
        b.Property(x => x.OffsiteUpload).HasColumnType("jsonb");
        b.Property(x => x.SecondaryCopies).HasColumnType("jsonb");
        b.Property(x => x.RestoreTestNote).HasMaxLength(2000);
        b.HasIndex(x => x.StartedAt);
    }
}

/// <param name="OffsiteUploadedAt">When its off-site copy was uploaded and checked; null while it is not off site.</param>
/// <param name="OffsiteKey">Its object key in the off-site bucket, once an upload was tried.</param>
/// <param name="OffsiteAttempts">How many times its upload was tried.</param>
/// <param name="OffsiteError">Why the last upload attempt failed.</param>
/// <param name="SecondaryCopies">Its copies on the second disk or NAS, each with the disk it is on; empty when none is known.</param>
/// <param name="RestoreTestedAt">When it was last restored into a scratch database to check it; null if never.</param>
/// <param name="RestoreTestPassed">Whether that test restore passed.</param>
/// <param name="RestoreTestNote">What that test restore found, or why it failed.</param>
public sealed record BackupRunDto(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, BackupKind Kind, BackupStatus Status, string? FileName,
    long? SizeBytes, string? Sha256, bool SecondaryCopied, string? Error, int FormatVersion, int? DocumentCount, DateTimeOffset? VerifiedAt,
    string? RecoveryKeyId, DateTimeOffset? OffsiteUploadedAt = null, string? OffsiteKey = null, int OffsiteAttempts = 0, string? OffsiteError = null,
    IReadOnlyList<BackupCopy>? SecondaryCopies = null, DateTimeOffset? RestoreTestedAt = null, bool? RestoreTestPassed = null, string? RestoreTestNote = null);

/// <summary>The backup start-up takes before it changes the schema (<see cref="Persistence.DatabaseInitializer"/>).</summary>
internal interface IPreMigrationBackup
{
    /// <summary>Takes it, without documents; not yet in the backup history.</summary>
    Task<BackupResult> CreatePreMigrationAsync(IReadOnlyList<string> appliedMigrations, CancellationToken cancellationToken);

    /// <summary>Adds it to the backup history, once the migrations are applied.</summary>
    Task RecordAsync(BackupResult result, CancellationToken cancellationToken);
}

/// <summary>A finished, verified backup file.</summary>
/// <param name="SecondaryDisk">The id of the disk it was copied to (<see cref="BackupDisk"/>), when it was copied and the disk is known.</param>
internal sealed record BackupResult(
    BackupKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string FileName,
    long SizeBytes,
    string Sha256,
    int? DocumentCount,
    DateTimeOffset VerifiedAt,
    bool SecondaryCopied,
    string? Warning,
    string RecoveryKeyId,
    string? SecondaryDisk = null);

/// <summary>
/// Encrypted backups (DED-16). Each one is a <see cref="BackupArchive"/> with the pg_dump of the database, an escrow copy of
/// the field encryption keys, the hospital CA, the erasure ledger and the index of the stored documents, encrypted with the
/// current recovery key, which administrators keep offline (<see cref="RecoveryKeyStore"/>) and the file names. The
/// documents themselves are kept once in the backup folder beside it (<see cref="BackupStore"/>). The file is written under
/// a temporary name, then read back in full and its database dump listed by pg_restore; only a backup that passes becomes
/// the finished file, is copied with the documents it needs to the secondary location (USB/NAS), is uploaded to the
/// off-site bucket when one is set up (<see cref="BackupOffsiteUploader"/>, within 15 minutes) and counts for retention
/// (<see cref="BackupRetention"/>). Restore with Restore-HC360.ps1, which runs the Server CLI's <c>backup restore</c>.
/// </summary>
internal sealed class BackupService(
    IDbContextFactory<HospitalDbContext> contextFactory,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    RecoveryKeyStore recoveryKeys,
    IFieldKeyEscrow keys,
    Auditing.AuditSealKey auditSealKey,
    IDocumentStoreLock documentsLock,
    IPostgresTools postgres,
    INotificationService notifications,
    TimeProvider time,
    ILogger<BackupService> logger,
    LanCertificateManager? certificates = null) : IPreMigrationBackup
{
    public const string FileExtension = ".hc360bak";
    internal const string PartialExtension = ".partial";

    /// <summary>
    /// Starts the part of a backup's note (<see cref="BackupRun.Error"/>) that says why its copy to the secondary directory
    /// failed. System health reads it back to tell a failed copy from one never attempted (<see cref="BackupCopies"/>).
    /// </summary>
    internal const string CopyFailedNote = "Secondary copy failed: ";

    /// <summary>
    /// Starts the note of a backup that was copied to the secondary directory, where the older copies could not be removed
    /// afterwards: an append-only share does not let the server delete. Not a failed copy.
    /// </summary>
    internal const string OldCopiesKeptNote = "Copied to the secondary directory; older copies there were not removed: ";

    /// <summary>
    /// Starts the note of a backup after which no unused document was removed from a backup folder, because an archive
    /// there has no list of its documents or a list that does not verify (<see cref="BackupStore.Prune"/>). Not a failed copy.
    /// </summary>
    internal const string UnusedKeptNote = "Unused documents were kept: ";

    /// <summary>
    /// Replaces the failed copy's note once the copy is made later, when the second disk or NAS is back
    /// (<see cref="BackupUpkeep"/>).
    /// </summary>
    internal const string CopiedLaterNote = "Copied to the secondary directory later, once it was back: ";

    /// <summary>
    /// How old the newest good backup may be before System health says no backup succeeded (<c>backup.stale</c>, critical):
    /// a night's backup missed, and the half day after it. A missed backup is caught up at once past it, even in the day
    /// (<see cref="BackupCatchUpJob"/>).
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);

    /// <summary>
    /// One backup at a time on this server: the nightly one, one taken by hand, the one before an update and a missed one
    /// caught up each run a pg_dump and read the whole database, so the one that comes second waits for the first. The
    /// backup folders' own lock (<see cref="BackupStore.Lock"/>) keeps out another server's backup.
    /// </summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Called at each step of a backup with the step's name, for the tests that stop a backup at every step as a crash would
    /// (BackupModelTests). Null in the server.
    /// </summary>
    internal Action<string>? Checkpoint { get; init; }

    private static readonly string ServerVersion =
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>A full backup (nightly or on demand), recorded in the backup history as it runs.</summary>
    public Task<BackupRunDto> RunAsync(BackupKind kind, Guid? triggeredBy, CancellationToken cancellationToken) =>
        RunAsync(kind, triggeredBy, lowPriority: false, cancellationToken);

    /// <summary>A full backup, recorded in the backup history as it runs.</summary>
    /// <param name="lowPriority">
    /// Gently, for a missed backup caught up while the hospital works (<see cref="BackupCatchUpJob"/>): pg_dump below
    /// normal priority, so the server's requests come first.
    /// </param>
    public async Task<BackupRunDto> RunAsync(BackupKind kind, Guid? triggeredBy, bool lowPriority, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await RunHeldAsync(kind, triggeredBy, lowPriority, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<BackupRunDto> RunHeldAsync(BackupKind kind, Guid? triggeredBy, bool lowPriority, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = new BackupRun { StartedAt = time.GetUtcNow(), Kind = kind, Status = BackupStatus.Running, TriggeredBy = triggeredBy };
        db.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
            var result = await CreateAsync(kind, run.StartedAt, includeDocuments: true, migrations, lowPriority, cancellationToken);
            Apply(run, result);
            await NoteDiskSwapAsync(db, run, result.SecondaryDisk, cancellationToken);
            logger.LogInformation("Backup {File} completed and verified ({Size:N0} bytes, {Documents} documents)", run.FileName, run.SizeBytes, run.DocumentCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = BackupStatus.Failed;
            run.Error = Truncate(ex.Message);
            logger.LogError(ex, "Backup failed");
            await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, "Backup failed", run.Error, "/admin/system", "system", "critical", cancellationToken);
        }

        run.CompletedAt ??= time.GetUtcNow();
        db.Add(AuditEventFor(run));
        await db.SaveChangesAsync(CancellationToken.None);
        if (run.Status == BackupStatus.Succeeded && !run.SecondaryCopied && options.Value.Backup.SecondaryDirectory is { Length: > 0 } secondary)
        {
            await TellNotCopiedAsync(run, secondary);
        }

        return ToDto(run);
    }

    /// <summary>
    /// A backup that was made but not copied to the secondary directory is on the server's own disk only. The
    /// administrators are told in the app, as of a failed backup, rather than when someone next opens System health, which
    /// warns too (<see cref="BackupCopies"/>). It is told once the run is recorded, so a failure to tell loses nothing.
    /// </summary>
    private async Task TellNotCopiedAsync(BackupRun run, string secondary)
    {
        try
        {
            await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, BackupCopies.NotCopiedTitle,
                BackupCopies.NotCopiedBody(run.FileName!, secondary, run.Error), "/admin/system", "system", "warning", CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not tell the administrators that backup {File} was not copied to {Directory}", run.FileName, secondary);
        }
    }

    /// <summary>
    /// The backup start-up takes before it changes the schema: the database and the key, without documents (migrations
    /// never touch them), written and verified without using the tables that are about to change. It is added to the
    /// backup history with <see cref="RecordAsync"/> once the migrations are applied.
    /// </summary>
    public async Task<BackupResult> CreatePreMigrationAsync(IReadOnlyList<string> appliedMigrations, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await CreateAsync(BackupKind.PreMigration, time.GetUtcNow(), includeDocuments: false, appliedMigrations, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RecordAsync(BackupResult result, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = new BackupRun { StartedAt = result.StartedAt, Kind = result.Kind };
        Apply(run, result);
        await NoteDiskSwapAsync(db, run, result.SecondaryDisk, cancellationToken);
        db.Add(run);
        db.Add(AuditEventFor(run));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A copy made to another disk than the copy before it: the disks were swapped, which the audit trail records
    /// (<c>backup.disk_swapped</c>), and System health counts each disk's time in place from (<see cref="BackupDisk"/>).
    /// </summary>
    private async Task NoteDiskSwapAsync(HospitalDbContext db, BackupRun run, string? disk, CancellationToken ct)
    {
        if (disk is null)
        {
            return;
        }

        var before = await db.Set<BackupRun>().AsNoTracking()
            .Where(r => r.Id != run.Id && r.SecondaryCopies != null && r.StartedAt < run.StartedAt)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => r.SecondaryCopies)
            .FirstOrDefaultAsync(ct);
        if (BackupCopies.Read(before).OrderBy(c => c.CopiedAt).LastOrDefault() is { Disk: { } previous } && previous != disk)
        {
            db.Add(new AuditEvent
            {
                OccurredAt = time.GetUtcNow(),
                Category = AuditCategories.Admin,
                Action = BackupDisk.SwappedAction,
                Severity = AuditSeverity.Info,
                Summary = $"Backup disk {disk} in place of {previous}: {run.FileName} was copied to it",
                UserId = run.TriggeredBy,
            });
            logger.LogInformation("The backup disk was swapped: {Disk} in place of {Previous}", disk, previous);
        }
    }

    internal static BackupRunDto ToDto(BackupRun r) =>
        new(r.Id, r.StartedAt, r.CompletedAt, r.Kind, r.Status, r.FileName, r.SizeBytes, r.Sha256, r.SecondaryCopied, r.Error, r.FormatVersion, r.DocumentCount, r.VerifiedAt,
            r.RecoveryKeyId, r.OffsiteUploadedAt, r.OffsiteKey, r.OffsiteAttempts, r.OffsiteError, BackupCopies.Read(r.SecondaryCopies), r.RestoreTestedAt,
            r.RestoreTestPassed, r.RestoreTestNote);

    internal Task<BackupResult> CreateAsync(BackupKind kind, DateTimeOffset startedAt, bool includeDocuments, IReadOnlyList<string> migrations, CancellationToken ct) =>
        CreateAsync(kind, startedAt, includeDocuments, migrations, lowPriority: false, ct);

    internal async Task<BackupResult> CreateAsync(
        BackupKind kind, DateTimeOffset startedAt, bool includeDocuments, IReadOnlyList<string> migrations, bool lowPriority, CancellationToken ct)
    {
        Directory.CreateDirectory(directories.Temp);
        Directory.CreateDirectory(directories.Backups);
        RemoveLeftoverDumps();
        var fileName = $"hc360-{startedAt:yyyyMMdd-HHmmss}-{kind.ToString().ToLowerInvariant()}{FileExtension}";
        var target = Path.Combine(directories.Backups, fileName);
        var partial = target + PartialExtension;
        var dump = Path.Combine(directories.Temp, $"{Guid.NewGuid():N}.dump");
        var recoveryKey = recoveryKeys.Current();
        var backupKey = recoveryKey.Key;
        var referencesKey = ReferencesKey();
        IAsyncDisposable? documentsHeld = null;
        try
        {
            // The backup folder keeps each stored document once (BackupStore, docs/design/incremental-backup.md); the
            // archive lists the documents it needs. Holding the folder keeps a second backup on this server out of it.
            var store = BackupStore.Open(directories.Backups, keys.Fingerprint, referencesKey, time);
            using var held = store.Lock();
            var hashesPath = Path.Combine(directories.Backups, BackupHashCache.FileName);
            var hashes = includeDocuments ? BackupHashCache.Load(hashesPath) : BackupHashCache.Empty();

            await EnsureRoomAsync(includeDocuments ? NewDocumentBytes(store, hashes) : 0, ct);

            // The key tables' rows, counted in the snapshot the dump holds, for a test restore to check (BackupRestoreTest).
            var rowCounts = await postgres.DumpAsync(dump, lowPriority, ct);
            Checkpoint?.Invoke("dumped");

            // The documents are listed after the database snapshot. The file store writes a document in full before its row
            // is committed and never removes it, so every document the dump refers to is on disk by now. One saved since is
            // not in the dump and is only an extra file in the backup. Moving documents to a new key rewrites them, so
            // that waits while they are hashed and copied.
            if (includeDocuments)
            {
                documentsHeld = await documentsLock.HoldSharedAsync(ct);
            }

            IReadOnlyList<BackupIndexedDocument>? index = null;
            var copied = 0;
            if (includeDocuments)
            {
                var documents = BackupArchive.ListDocuments(directories.Files);
                var listed = new List<BackupIndexedDocument>(documents.Count);
                foreach (var document in documents)
                {
                    var id = await hashes.HashAsync(directories.Files, document, ct);
                    if (await store.AddAsync(held, Path.Combine(directories.Files, document.RelativePath), id, ct))
                    {
                        copied++;
                    }

                    listed.Add(new BackupIndexedDocument(document.RelativePath, id, document.Length));
                    Checkpoint?.Invoke("document copied");
                }

                hashes.KeepOnly(documents.Select(d => d.RelativePath).ToHashSet(StringComparer.Ordinal));
                index = listed;
            }

            // The erasure ledger after the dump: an erasure committed since is in the ledger and not the dump, and a restore
            // of this backup carries it out again. And the hospital CA the devices trust. The backup before an update
            // carries neither: it is for going back on this machine, whose ledger and CA the update leaves as they are, and
            // is written for the version updated from (below), which may know neither.
            var extras = includeDocuments
                ? new BackupExtras(ErasureLedgerFiles.ReadForBackup(directories, options.Value.Backup), ExportHospitalCa())
                : new BackupExtras();

            // Every key of the ring is escrowed, and listed in the manifest, so data encrypted with any of them restores. The
            // ring is exported after the documents are listed: a key a rotation adds meanwhile is in it, and a document
            // listed is never encrypted with a key retired since (retiring needs nothing left on it).
            var ring = keys.ExportRing();
            var manifest = new BackupManifest(BackupArchive.Format, kind.ToString(), startedAt, ServerVersion, postgres.DatabaseName, migrations,
                includeDocuments, 0, 0, keys.Fingerprint,
                ring.Keys.Count == 0 ? null : ring.Keys.Select(FieldMasterKey.Fingerprint).ToList(), ring.Current, auditSealKey.Fingerprint,
                RowCounts: rowCounts.Count == 0 ? null : rowCounts);

            string sha256;
            var masterKey = keys.ExportMasterKey();
            var sealKey = auditSealKey.Export();
            try
            {
                await using var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await using var encryptor = BackupEncryption.CreateEncryptor(file, backupKey);
                if (includeDocuments)
                {
                    await BackupArchive.WriteIndexedAsync(encryptor, manifest, masterKey, ring.Keys, sealKey, dump, index, extras, ct);
                }
                else
                {
                    // The backup before an update has no documents, and is restored by the version updated from when the
                    // update is undone (DEPLOYMENT.md section 7, "Going back"): format 2, which every earlier version reads.
                    await BackupArchive.WriteAsync(encryptor, manifest with { DocumentCount = 0, DocumentBytes = 0 }, masterKey, ring.Keys, sealKey, dump,
                        directories.Files, [], ct);
                }

                await encryptor.CompleteAsync(ct);
                sha256 = encryptor.Sha256!;
                Checkpoint?.Invoke("archive written");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
                CryptographicOperations.ZeroMemory(sealKey);
                if (extras.HospitalCa is { } ca)
                {
                    CryptographicOperations.ZeroMemory(ca.Pkcs12);
                }

                foreach (var key in ring.Keys)
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                if (documentsHeld is not null)
                {
                    await documentsHeld.DisposeAsync();
                    documentsHeld = null;
                }
            }

            File.Delete(dump);
            var warning = await VerifyAsync(partial, backupKey, ct);
            Checkpoint?.Invoke("archive verified");
            CheckDocumentsPresent(store, index ?? []);

            // The list first, then the archive: an archive in the folder always has its list, so its documents are never
            // taken for unused.
            var ids = (index ?? []).Select(d => d.BlobId).ToList();
            store.WriteReferences(held, fileName, ids, referencesKey);
            Checkpoint?.Invoke("list written");
            File.Move(partial, target);
            Checkpoint?.Invoke("archive in place");
            var verifiedAt = time.GetUtcNow();
            if (includeDocuments)
            {
                hashes.Save(hashesPath);
                logger.LogInformation("Backup {File}: {Copied} of {Documents} documents were new and copied to the backup folder", fileName, copied, ids.Count);
            }

            var secondaryCopied = false;
            string? disk = null;
            if (options.Value.Backup.SecondaryDirectory is { Length: > 0 } secondary)
            {
                (secondaryCopied, var note, disk) = await CopyToSecondaryAsync(store, target, sha256, index ?? [], secondary, referencesKey, ct);
                if (note is not null)
                {
                    warning = Join(warning, note);
                }
            }

            Checkpoint?.Invoke("before prune");
            try
            {
                var pruned = store.Prune(held, options.Value.Backup.Retention, Zone, referencesKey);
                if (pruned.SweepSkipped is { } skipped)
                {
                    logger.LogWarning("Unused documents were not removed from {Directory}: {Reason}", directories.Backups, skipped);
                    warning = Join(warning, UnusedKeptNote + skipped);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The backup is made all the same; the next one, or the night's upkeep, tries again.
                logger.LogWarning(ex, "Backups that retention no longer keeps could not all be removed from {Directory}", directories.Backups);
            }

            return new BackupResult(kind, startedAt, time.GetUtcNow(), fileName, new FileInfo(target).Length, sha256,
                includeDocuments ? ids.Count : null, verifiedAt, secondaryCopied, warning, recoveryKey.KeyId, disk);
        }
        finally
        {
            if (documentsHeld is not null)
            {
                await documentsHeld.DisposeAsync();
            }

            CryptographicOperations.ZeroMemory(backupKey);
            CryptographicOperations.ZeroMemory(referencesKey);
            File.Delete(dump);
            File.Delete(partial);
        }
    }

    private TimeZoneInfo Zone => PlatformServiceCollectionExtensions.ResolveTimeZone(options.Value.TimeZone);

    /// <summary>
    /// The hospital CA for the backup to carry, inside its encryption, so a server restored from it on new hardware serves a
    /// certificate the devices already trust (docs/security/SECURITY.md, "Backups and keys"); null when this server has
    /// none. One that cannot be read is left out, and said in the log: the backup of the database matters more.
    /// </summary>
    private ExportedHospitalCa? ExportHospitalCa()
    {
        try
        {
            return certificates?.ExportForBackup();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The hospital CA could not be read, so this backup does not carry it");
            return null;
        }
    }

    /// <summary>
    /// The key that seals each archive's list of documents (<see cref="BackupStore.ReferencesKeyPurpose"/>): derived from
    /// the original field key, which is never retired, so it is the same on every server restored from these backups.
    /// </summary>
    private byte[] ReferencesKey() => ReferencesKeyOf(keys);

    /// <inheritdoc cref="ReferencesKey"/>
    internal static byte[] ReferencesKeyOf(IFieldKeyEscrow keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var masterKey = keys.ExportMasterKey();
        try
        {
            return FieldMasterKey.DeriveKey(masterKey, BackupStore.ReferencesKeyPurpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>
    /// About how many bytes of documents tonight's backup will copy: those the hash cache does not know unchanged, or knows
    /// but the folder does not have. Nothing is read, so the estimate is quick; it errs towards counting a document.
    /// </summary>
    private long NewDocumentBytes(BackupStore store, BackupHashCache hashes) =>
        BackupArchive.ListDocuments(directories.Files)
            .Where(d => hashes.Lookup(d.RelativePath, new FileInfo(Path.Combine(directories.Files, d.RelativePath))) is not { } id || !store.Contains(id, d.Length))
            .Sum(d => d.Length);

    /// <summary>Every document the archive lists is in the folder in full. Its content was hashed as it was copied in.</summary>
    private static void CheckDocumentsPresent(BackupStore store, IReadOnlyList<BackupIndexedDocument> index)
    {
        if (index.FirstOrDefault(d => !store.Contains(d.BlobId, d.Length)) is { } missing)
        {
            throw new InvalidOperationException(
                $"The backup was written but the document {missing.RelativePath} is missing from the backup folder, so it was discarded.");
        }
    }

    /// <summary>
    /// Copies a finished backup to the secondary directory (<see cref="BackupStore.CopyArchiveAsync"/>): the documents it
    /// needs that are not there yet (from the primary folder, hashed again as they are copied), then its list, then the
    /// archive, hashed as it is read and under a temporary name until complete. Then the archives there that retention no
    /// longer keeps, and the documents none of the remaining ones needs, are removed.
    /// </summary>
    /// <returns>Whether it was copied, the backup's note about it, and the id of the disk it went to (<see cref="BackupDisk"/>).</returns>
    private async Task<(bool Copied, string? Note, string? Disk)> CopyToSecondaryAsync(
        BackupStore primary, string archive, string sha256, IReadOnlyList<BackupIndexedDocument> index, string secondary, byte[] referencesKey, CancellationToken ct)
    {
        BackupStore? store = null;
        BackupStoreLock? held = null;
        try
        {
            var outcome = await CopyToSecondary(
                async () =>
                {
                    store = BackupStore.Open(secondary, keys.Fingerprint, referencesKey, time, options.Value.Backup.SecondaryAppendOnly);
                    held = store.Lock();
                    var ids = index.Select(d => d.BlobId).Distinct(StringComparer.Ordinal).ToList();

                    // The night's upkeep reads the copy back (BackupUpkeep), as it does every copy not read back yet.
                    if (await store.CopyArchiveAsync(held, primary, archive, sha256, ids, referencesKey, readBack: false, Checkpoint, ct) is { } problem)
                    {
                        throw new IOException(problem);
                    }
                },
                () => store!.AppendOnly
                    ? throw new UnauthorizedAccessException(AppendOnlyReason(store.Generation!))
                    : store.Prune(held!, options.Value.Backup.Retention, Zone, referencesKey),
                secondary,
                logger);

            // Each disk is given an id on its first copy, so swapped disks are told apart.
            var disk = outcome.Copied ? BackupDisk.Identify(secondary, time.GetUtcNow(), logger) : null;
            var note = store is not null && FileSystemNote(store.Directory) is { } fileSystem ? Join(outcome.Note, fileSystem) : outcome.Note;
            return (outcome.Copied, note, disk);
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>
    /// The outcome of a copy to the secondary directory, as the backup's note says it. A copy that could not be made is
    /// <see cref="CopyFailedNote"/>, which System health warns of (<see cref="BackupCopies"/>). A share that let the server
    /// add the copy but refused to delete older ones (an append-only share, DEPLOYMENT.md section 8) is not a failed copy:
    /// <see cref="OldCopiesKeptNote"/>. Documents kept because a list was missing or did not verify are
    /// <see cref="UnusedKeptNote"/>.
    /// </summary>
    internal static async Task<(bool Copied, string? Note)> CopyToSecondary(Func<Task> copy, Func<BackupPruneResult> removeOld, string secondary, ILogger logger)
    {
        try
        {
            await copy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(ex, "Backup copy to secondary location {Directory} failed", secondary);
            return (false, CopyFailedNote + ex.Message);
        }

        try
        {
            var pruned = removeOld();
            return (true, pruned.SweepSkipped is { } skipped ? UnusedKeptNote + skipped : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation("Older backup copies in {Directory} were not removed: {Reason}", secondary, ex.Message);
            return (true, OldCopiesKeptNote + ex.Message);
        }
    }

    /// <summary>Why nothing is removed from an append-only secondary folder, in the backup's note.</summary>
    private string AppendOnlyReason(string generation) => string.Create(CultureInfo.InvariantCulture,
        $"the folder only lets HC360 add files, so its own retention removes old copies (keep each file {BackupRetention.AppendOnlyMinimumDays(options.Value.Backup.Retention)} days or more); tonight's documents went into {generation}.");

    /// <summary>A note when the secondary disk is FAT or exFAT, which suit a backup folder badly; null otherwise.</summary>
    private string? FileSystemNote(string directory)
    {
        string? format;
        try
        {
            format = new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!BackupStore.UnsuitableFileSystem(format))
        {
            return null;
        }

        logger.LogWarning("The backup disk {Directory} is {Format}; format it NTFS", directory, format);
        return string.Create(CultureInfo.InvariantCulture,
            $"The secondary disk is {format}, which has no journal and wastes space on every stored document: format it NTFS.");
    }

    /// <summary>
    /// A server stopped in the middle of a backup (a power cut, a killed process) leaves its database dump behind, and a
    /// dump is not encrypted. Leftovers are removed before the next backup starts; a dump still being written is never
    /// this old.
    /// </summary>
    private void RemoveLeftoverDumps()
    {
        foreach (var dump in LeftoverDumps(directories.Temp, time.GetUtcNow()))
        {
            try
            {
                dump.Delete();
                logger.LogWarning("Removed the database dump {File} left behind by an interrupted backup", dump.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the leftover database dump {File}", dump.Name);
            }
        }
    }

    internal static IEnumerable<FileInfo> LeftoverDumps(string temp, DateTimeOffset now) =>
        new DirectoryInfo(temp).EnumerateFiles("*.dump").Where(f => f.LastWriteTimeUtc < now.UtcDateTime - TimeSpan.FromHours(12)).ToList();

    /// <summary>
    /// A backup needs room for the database dump twice (the archive, and the copy the check reads back), the documents and
    /// 1 GB to spare. Refusing early keeps the disk from filling up, for example when a failing update makes Windows restart
    /// the service again and again. On a network share the free space is not known, so it is not checked.
    /// </summary>
    private async Task EnsureRoomAsync(long documentBytes, CancellationToken ct)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directories.Backups))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        var needed = (2 * await postgres.GetDatabaseSizeAsync(ct)) + documentBytes + (1L << 30);
        if (free < needed)
        {
            throw new InvalidOperationException(
                $"Not enough free disk space for a backup in {directories.Backups}: {free / (1 << 20):N0} MB free, about {needed / (1 << 20):N0} MB needed. " +
                "Free up space, or move the backups to a larger disk (HealthCare360:Backup:Directory).");
        }
    }

    /// <summary>
    /// Reads the whole file back as a restore would: every chunk authenticated, the archive checked against its manifest,
    /// then the database dump listed by pg_restore. Throws when the backup could not be restored.
    /// </summary>
    /// <returns>A note when pg_restore is not installed, so only the archive could be checked.</returns>
    private async Task<string?> VerifyAsync(string backupFile, byte[] backupKey, CancellationToken ct)
    {
        var dump = Path.Combine(directories.Temp, $"verify-{Guid.NewGuid():N}.dump");
        try
        {
            await using (var input = new FileStream(backupFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var payload = await BackupEncryption.OpenAsync(input, backupKey, ct))
            {
                var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(DatabaseDumpPath: dump), ct);
                CryptographicOperations.ZeroMemory(contents.MasterKey);
            }

            return await postgres.TryValidateDumpAsync(dump, ct) ? null : "Checked without pg_restore: the database dump was not listed.";
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            throw new InvalidOperationException("The backup was written but could not be read back, so it was discarded: " + ex.Message, ex);
        }
        finally
        {
            File.Delete(dump);
        }
    }

    private static void Apply(BackupRun run, BackupResult result)
    {
        run.Status = BackupStatus.Succeeded;
        run.CompletedAt = result.CompletedAt;
        run.FileName = result.FileName;
        run.SizeBytes = result.SizeBytes;
        run.Sha256 = result.Sha256;
        run.SecondaryCopied = result.SecondaryCopied;
        run.Error = result.Warning is null ? null : Truncate(result.Warning);
        run.FormatVersion = BackupEncryption.KeyedVersion;
        run.DocumentCount = result.DocumentCount;
        run.VerifiedAt = result.VerifiedAt;
        run.RecoveryKeyId = result.RecoveryKeyId;
        run.SecondaryCopies = result.SecondaryCopied ? BackupCopies.With(null, new BackupCopy(result.SecondaryDisk, result.CompletedAt)) : null;
    }

    private static AuditEvent AuditEventFor(BackupRun run) => new()
    {
        OccurredAt = run.CompletedAt ?? run.StartedAt,
        Category = AuditCategories.Admin,
        Action = run.Status == BackupStatus.Succeeded ? "backup.succeeded" : "backup.failed",
        Severity = run.Status == BackupStatus.Succeeded ? AuditSeverity.Info : AuditSeverity.Critical,
        Summary = run.Status == BackupStatus.Succeeded ? $"Backup {run.FileName}" : run.Error,
        UserId = run.TriggeredBy,
    };

    private static string Truncate(string text) => text.Length > 2000 ? text[..2000] : text;

    private static string Join(string? first, string second) => first is null ? second : first + " " + second;
}

[DisallowConcurrentExecution]
internal sealed class BackupJob(BackupService backups) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await backups.RunAsync(BackupKind.Scheduled, null, cancellationToken);
}

/// <summary>When a missed nightly backup is taken (<see cref="BackupCatchUpJob.Decide"/>).</summary>
internal enum BackupCatchUp
{
    /// <summary>Not now: nothing was missed, or the next window's nightly backup takes it.</summary>
    None,

    /// <summary>Now, inside a backup window, when the hospital is quiet.</summary>
    Now,

    /// <summary>Now, outside the window, as the newest good backup is stale: gently, one pg_dump below normal priority.</summary>
    NowGently,
}

/// <summary>
/// The nightly backup the server was off for: the job scheduler keeps its schedule in memory, so a run missed while the
/// server was stopped (a power cut, an update, a reboot at the wrong hour) is never fired late. Every quarter of an hour
/// this checks whether the schedule fell due since the last full backup was attempted and, if so, whether to take it now
/// (<see cref="Decide"/>): inside the backup window that follows each scheduled time (<see cref="Window"/>, 01:30 to
/// 05:30 by default), at once; outside it, a backup at 10:00 would load the server while the hospital works, so the next
/// window's nightly backup takes it, unless the newest good backup is older than <see cref="BackupService.StaleAfter"/>,
/// when it is taken at once all the same, gently: one pg_dump at a time, below normal priority. A new installation that
/// has never attempted one waits for its schedule. The checks are on the clock, so every server that may run the jobs
/// names the same times and each is claimed once for the hospital (<see cref="JobSlotClaim"/>).
/// </summary>
[DisallowConcurrentExecution]
internal sealed class BackupCatchUpJob(
    BackupService backups,
    IDbContextFactory<HospitalDbContext> contexts,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<BackupCatchUpJob> logger) : IJob
{
    /// <summary>Every quarter of an hour, at 7, 22, 37 and 52 minutes past: clear of a nightly backup set on the hour or half hour.</summary>
    public const string Schedule = "0 7/15 * * * ?";

    /// <summary>The backup window: the hours after each scheduled backup, when the hospital is quietest.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(4);

    /// <summary>
    /// How late a scheduled backup is before it counts as missed: the night's own backup starts on the minute and is in the
    /// backup history at once, so a check just after it never takes it a second time.
    /// </summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(10);

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        BackupCatchUp decision;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // A backup started since the jobs started here is under way (and is the backup); one still marked as running
            // from before was cut off with its server, by the power cut or update that made the nightly backup missed.
            var full = db.Set<BackupRun>().Where(r => r.Kind == BackupKind.Scheduled || r.Kind == BackupKind.Manual);
            var jobsStarted = (await context.Scheduler.GetMetadata(cancellationToken)).RunningSince ?? now;
            if (BackupService.Gate.CurrentCount == 0
                || await full.AnyAsync(r => r.Status == BackupStatus.Running && r.StartedAt >= jobsStarted, cancellationToken))
            {
                return;
            }

            var lastAttempt = await full.MaxAsync(r => (DateTimeOffset?)r.StartedAt, cancellationToken);
            var lastGood = await db.Set<BackupRun>().Where(r => r.Status == BackupStatus.Succeeded).MaxAsync(r => r.CompletedAt, cancellationToken);
            var zone = PlatformServiceCollectionExtensions.ResolveTimeZone(options.Value.TimeZone);
            decision = Decide(options.Value.Backup.Schedule, zone, lastAttempt, lastGood, now);
            if (decision == BackupCatchUp.None)
            {
                return;
            }

            if (decision == BackupCatchUp.Now)
            {
                logger.LogWarning("The scheduled backup was missed while the server was stopped (last attempted {LastAttempt}); taking it now", lastAttempt);
            }
            else
            {
                logger.LogWarning(
                    "The scheduled backup was missed (last attempted {LastAttempt}) and the newest good backup, of {LastGood}, is over {Hours} hours old; taking it now, at low priority",
                    lastAttempt, lastGood, BackupService.StaleAfter.TotalHours);
            }
        }

        await backups.RunAsync(BackupKind.Scheduled, null, lowPriority: decision == BackupCatchUp.NowGently, cancellationToken);
    }

    /// <summary>
    /// Whether, and how, to take the backup <paramref name="schedule"/> missed: not at all when it did not fall due at least
    /// <see cref="MissedAfter"/> after <paramref name="lastAttempt"/>; now when <paramref name="now"/> is inside the
    /// <see cref="Window"/> after a scheduled time; now, gently, when the newest good backup (<paramref name="lastGood"/>,
    /// when it finished) is older than <see cref="BackupService.StaleAfter"/>, or there is none; otherwise not yet: the next
    /// window's nightly backup takes it, or a later check once the newest good backup turns stale.
    /// </summary>
    public static BackupCatchUp Decide(string schedule, TimeZoneInfo zone, DateTimeOffset? lastAttempt, DateTimeOffset? lastGood, DateTimeOffset now)
    {
        if (!Missed(schedule, zone, lastAttempt, now))
        {
            return BackupCatchUp.None;
        }

        if (new CronExpression(schedule, zone).GetNextValidTimeAfter(now - Window) is { } scheduled && scheduled <= now)
        {
            return BackupCatchUp.Now;
        }

        return lastGood is null || now - lastGood > BackupService.StaleAfter ? BackupCatchUp.NowGently : BackupCatchUp.None;
    }

    /// <summary>Whether <paramref name="schedule"/> fell due after <paramref name="lastAttempt"/>, and at least <see cref="MissedAfter"/> before <paramref name="now"/>.</summary>
    public static bool Missed(string schedule, TimeZoneInfo zone, DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is { } last
        && new CronExpression(schedule, zone).GetNextValidTimeAfter(last) is { } due
        && due <= now - MissedAfter;
}

internal static class BackupEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/backups").WithTags("Administration").RequirePermission(PlatformPermissions.BackupsManage);

        group.MapGet("/", async (HospitalDbContext db, CancellationToken ct) =>
            TypedResults.Ok((await db.Set<BackupRun>().AsNoTracking().OrderByDescending(r => r.StartedAt).Take(100).ToListAsync(ct)).Select(BackupService.ToDto)));

        group.MapPost("/", async (HospitalDbContext db, IServiceScopeFactory scopes, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
        {
            var recent = time.GetUtcNow().AddHours(-3);
            if (await db.Set<BackupRun>().AnyAsync(r => r.Status == BackupStatus.Running && r.StartedAt > recent, ct))
            {
                return Error.Conflict("backup.running", "A backup is already running.").ToProblem();
            }

            var userId = user.UserId;
            _ = Task.Run(async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, userId, CancellationToken.None);
            }, CancellationToken.None);

            return TypedResults.Accepted("/api/v1/admin/backups");
        });

        // The recovery key opens the backups, and each backup carries the field encryption keys and the documents, so with
        // it one backup file restores everything on replacement hardware. Whoever runs backups sees which key new ones use;
        // a new key is shown once, to the holder of admin.system.manage who makes it after giving their password again,
        // and replaces the current one once they type it back from their copy (ASVS V11.1.1, V13.3.2).
        group.MapGet("/recovery-key", async (RecoveryKeyService keys, CancellationToken ct) => TypedResults.Ok(await keys.StatusAsync(ct)));
        group.MapPost("/recovery-key", async (ReauthenticateRequest request, RecoveryKeyService keys, CancellationToken ct) =>
                (await keys.CreateAsync(request, ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage)
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<ReauthenticateRequest>();
        group.MapPost("/recovery-key/{keyId}/confirm", async (string keyId, ConfirmRecoveryKeyRequest request, RecoveryKeyService keys, CancellationToken ct) =>
                (await keys.ConfirmAsync(keyId, request, ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage)
            .WithValidation<ConfirmRecoveryKeyRequest>();

        // Two people should hold the key, one copy outside the building, and its copies are checked every quarter: any
        // administrator who runs backups types the current key in from their copy to show they hold it. Nothing changes and
        // the key is never shown; the proof (who, and when) is what System health counts (RecoveryKeyCustody).
        group.MapPost("/recovery-key/prove", async (ConfirmRecoveryKeyRequest request, RecoveryKeyService keys, CancellationToken ct) =>
                (await keys.ProveAsync(request, ct)).ToOk())
            .WithValidation<ConfirmRecoveryKeyRequest>();
    }
}
