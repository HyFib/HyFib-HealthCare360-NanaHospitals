using System.Reflection;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
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
    /// encryption key and, for full backups, the stored documents (<see cref="BackupArchive"/>).
    /// </summary>
    public int FormatVersion { get; set; } = BackupEncryption.LegacyVersion;

    /// <summary>Documents in the backup; null when it holds the database only (older backups, and those taken before an update).</summary>
    public int? DocumentCount { get; set; }

    /// <summary>When the finished file was read back in full, checked against its manifest and its dump listed by pg_restore.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }
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
        b.HasIndex(x => x.StartedAt);
    }
}

public sealed record BackupRunDto(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, BackupKind Kind, BackupStatus Status, string? FileName,
    long? SizeBytes, string? Sha256, bool SecondaryCopied, string? Error, int FormatVersion, int? DocumentCount, DateTimeOffset? VerifiedAt);

/// <summary>A finished, verified backup file.</summary>
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
    string? Warning);

/// <summary>
/// Encrypted backups (DED-16). Each one is a single <see cref="BackupArchive"/> with the pg_dump of the database, an escrow
/// copy of the field encryption key and the stored documents, encrypted with the backup key, which is also the recovery
/// key administrators keep offline. The file is written under a temporary name, then read back in full and its database
/// dump listed by pg_restore; only a backup that passes becomes the finished file, is copied to the secondary location
/// (USB/NAS) and counts for retention. Restore with Restore-HC360.ps1, which runs the Server CLI's <c>backup restore</c>.
/// </summary>
internal sealed class BackupService(
    IDbContextFactory<HospitalDbContext> contextFactory,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    IFieldProtector protector,
    IFieldKeyEscrow keys,
    IDocumentStoreLock documentsLock,
    IPostgresTools postgres,
    INotificationService notifications,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    public const string FileExtension = ".hc360bak";
    private const string PartialExtension = ".partial";

    private static readonly string ServerVersion =
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>A full backup (nightly or on demand), recorded in the backup history as it runs.</summary>
    public async Task<BackupRunDto> RunAsync(BackupKind kind, Guid? triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = new BackupRun { StartedAt = time.GetUtcNow(), Kind = kind, Status = BackupStatus.Running, TriggeredBy = triggeredBy };
        db.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
            Apply(run, await CreateAsync(kind, run.StartedAt, includeDocuments: true, migrations, cancellationToken));
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
        return ToDto(run);
    }

    /// <summary>
    /// The backup start-up takes before it changes the schema: the database and the key, without documents (migrations
    /// never touch them), written and verified without using the tables that are about to change. It is added to the
    /// backup history with <see cref="RecordAsync"/> once the migrations are applied.
    /// </summary>
    public Task<BackupResult> CreatePreMigrationAsync(IReadOnlyList<string> appliedMigrations, CancellationToken cancellationToken) =>
        CreateAsync(BackupKind.PreMigration, time.GetUtcNow(), includeDocuments: false, appliedMigrations, cancellationToken);

    public async Task RecordAsync(BackupResult result, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = new BackupRun { StartedAt = result.StartedAt, Kind = result.Kind };
        Apply(run, result);
        db.Add(run);
        db.Add(AuditEventFor(run));
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static BackupRunDto ToDto(BackupRun r) =>
        new(r.Id, r.StartedAt, r.CompletedAt, r.Kind, r.Status, r.FileName, r.SizeBytes, r.Sha256, r.SecondaryCopied, r.Error, r.FormatVersion, r.DocumentCount, r.VerifiedAt);

    internal async Task<BackupResult> CreateAsync(BackupKind kind, DateTimeOffset startedAt, bool includeDocuments, IReadOnlyList<string> migrations, CancellationToken ct)
    {
        Directory.CreateDirectory(directories.Temp);
        Directory.CreateDirectory(directories.Backups);
        RemoveLeftoverDumps();
        var fileName = $"hc360-{startedAt:yyyyMMdd-HHmmss}-{kind.ToString().ToLowerInvariant()}{FileExtension}";
        var target = Path.Combine(directories.Backups, fileName);
        var partial = target + PartialExtension;
        var dump = Path.Combine(directories.Temp, $"{Guid.NewGuid():N}.dump");
        var backupKey = protector.DeriveKey("backup");
        IAsyncDisposable? documentsHeld = null;
        try
        {
            await EnsureRoomAsync(includeDocuments ? BackupArchive.ListDocuments(directories.Files).Sum(d => d.Length) : 0, ct);
            await postgres.DumpAsync(dump, ct);

            // The documents are listed after the database snapshot. The file store writes a document in full before its row
            // is committed and never removes it, so every document the dump refers to is on disk by now. One saved since is
            // not in the dump and is only an extra file in the backup. Moving documents to a new key rewrites them, so
            // that waits while they are copied.
            if (includeDocuments)
            {
                documentsHeld = await documentsLock.HoldSharedAsync(ct);
            }

            var documents = includeDocuments ? BackupArchive.ListDocuments(directories.Files) : [];

            // Every key of the ring is escrowed, and listed in the manifest, so data encrypted with any of them restores.
            var ring = keys.ExportRing();
            var manifest = new BackupManifest(BackupArchive.Format, kind.ToString(), startedAt, ServerVersion, postgres.DatabaseName, migrations,
                includeDocuments, documents.Count, documents.Sum(d => d.Length), keys.Fingerprint,
                ring.Keys.Count == 0 ? null : ring.Keys.Select(FieldMasterKey.Fingerprint).ToList(), ring.Current);

            string sha256;
            var masterKey = keys.ExportMasterKey();
            try
            {
                await using var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await using var encryptor = BackupEncryption.CreateEncryptor(file, backupKey);
                await BackupArchive.WriteAsync(encryptor, manifest, masterKey, ring.Keys, dump, directories.Files, documents, ct);
                await encryptor.CompleteAsync(ct);
                sha256 = encryptor.Sha256!;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
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
            File.Move(partial, target);
            var verifiedAt = time.GetUtcNow();

            var secondaryCopied = false;
            if (options.Value.Backup.SecondaryDirectory is { Length: > 0 } secondary)
            {
                try
                {
                    Directory.CreateDirectory(secondary);
                    File.Copy(target, Path.Combine(secondary, fileName), overwrite: true);
                    secondaryCopied = true;
                    CleanupOld(secondary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Backup copy to secondary location {Directory} failed", secondary);
                    warning = Join(warning, "Secondary copy failed: " + ex.Message);
                }
            }

            CleanupOld(directories.Backups);
            return new BackupResult(kind, startedAt, time.GetUtcNow(), fileName, new FileInfo(target).Length, sha256,
                includeDocuments ? documents.Count : null, verifiedAt, secondaryCopied, warning);
        }
        finally
        {
            if (documentsHeld is not null)
            {
                await documentsHeld.DisposeAsync();
            }

            File.Delete(dump);
            File.Delete(partial);
        }
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
        run.FormatVersion = BackupEncryption.ArchiveVersion;
        run.DocumentCount = result.DocumentCount;
        run.VerifiedAt = result.VerifiedAt;
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

    private void CleanupOld(string directory)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(options.Value.Backup.RetentionDays, 1));
        foreach (var file in Directory.EnumerateFiles(directory, "*" + FileExtension))
        {
            if (File.GetCreationTimeUtc(file) < cutoff)
            {
                File.Delete(file);
            }
        }

        // Left behind only when the server stopped in the middle of a backup.
        var stale = time.GetUtcNow().UtcDateTime.AddDays(-1);
        foreach (var file in Directory.EnumerateFiles(directory, "*" + FileExtension + PartialExtension))
        {
            if (File.GetLastWriteTimeUtc(file) < stale)
            {
                File.Delete(file);
            }
        }
    }
}

[DisallowConcurrentExecution]
internal sealed class BackupJob(BackupService backups) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await backups.RunAsync(BackupKind.Scheduled, null, cancellationToken);
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

        // The recovery key opens every backup, and each backup carries the field encryption key and the documents, so with
        // it one backup file restores everything on replacement hardware. Export it once and store it offline (safe/locker).
        group.MapGet("/recovery-key", async (IFieldProtector protector, IAuditLogger audit, CancellationToken ct) =>
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "backup.recovery_key_exported", "Backup recovery key displayed")
            {
                Severity = AuditSeverity.Critical,
            }, ct);
            return TypedResults.Ok(new { key = Convert.ToBase64String(protector.DeriveKey("backup")) });
        });
    }
}
