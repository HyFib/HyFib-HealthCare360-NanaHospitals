using System.Buffers.Binary;
using System.Diagnostics;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
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
    long? SizeBytes, string? Sha256, bool SecondaryCopied, string? Error);

/// <summary>
/// Encrypted PostgreSQL backups (DED-16): <c>pg_dump -Fc</c> → chunked AES-256-GCM (<see cref="BackupEncryption"/>)
/// → optional secondary copy (USB/NAS) → retention cleanup. Restore with the Server CLI
/// (<c>backup decrypt</c>) and <c>pg_restore</c>, using the recovery key exported from the admin console.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<HospitalDbContext> contextFactory,
    IConfiguration configuration,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    IFieldProtector protector,
    INotificationService notifications,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    public const string FileExtension = ".hc360bak";

    public async Task<BackupRunDto> RunAsync(BackupKind kind, Guid? triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = new BackupRun { StartedAt = time.GetUtcNow(), Kind = kind, Status = BackupStatus.Running, TriggeredBy = triggeredBy };
        db.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var tempFile = Path.Combine(directories.Temp, $"{run.Id:N}.dump");
        try
        {
            Directory.CreateDirectory(directories.Temp);
            Directory.CreateDirectory(directories.Backups);
            await DumpAsync(tempFile, cancellationToken);

            var fileName = $"hc360-{run.StartedAt:yyyyMMdd-HHmmss}-{kind.ToString().ToLowerInvariant()}{FileExtension}";
            var target = Path.Combine(directories.Backups, fileName);
            var key = protector.DeriveKey("backup");
            await using (var input = File.OpenRead(tempFile))
            await using (var output = File.Create(target))
            {
                await BackupEncryption.EncryptAsync(input, output, key, cancellationToken);
            }

            run.FileName = fileName;
            run.SizeBytes = new FileInfo(target).Length;
            await using (var hashStream = File.OpenRead(target))
            {
                run.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            }

            if (options.Value.Backup.SecondaryDirectory is { Length: > 0 } secondary)
            {
                try
                {
                    Directory.CreateDirectory(secondary);
                    File.Copy(target, Path.Combine(secondary, fileName), overwrite: true);
                    run.SecondaryCopied = true;
                    CleanupOld(secondary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Backup copy to secondary location {Directory} failed", secondary);
                    run.Error = "Secondary copy failed: " + ex.Message;
                }
            }

            CleanupOld(directories.Backups);
            run.Status = BackupStatus.Succeeded;
            logger.LogInformation("Backup {File} completed ({Size:N0} bytes)", fileName, run.SizeBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = BackupStatus.Failed;
            run.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            logger.LogError(ex, "Backup failed");
            await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, "Backup failed", run.Error, "/admin/system", "system", "critical", cancellationToken);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }

        run.CompletedAt = time.GetUtcNow();
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            OccurredAt = run.CompletedAt.Value,
            Category = AuditCategories.Admin,
            Action = run.Status == BackupStatus.Succeeded ? "backup.succeeded" : "backup.failed",
            Severity = run.Status == BackupStatus.Succeeded ? AuditSeverity.Info : AuditSeverity.Critical,
            Summary = run.Status == BackupStatus.Succeeded ? $"Backup {run.FileName}" : run.Error,
            UserId = triggeredBy,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return ToDto(run);
    }

    internal static BackupRunDto ToDto(BackupRun r) =>
        new(r.Id, r.StartedAt, r.CompletedAt, r.Kind, r.Status, r.FileName, r.SizeBytes, r.Sha256, r.SecondaryCopied, r.Error);

    private async Task DumpAsync(string outputFile, CancellationToken ct)
    {
        var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Hospital"));
        var startInfo = new ProcessStartInfo(ResolvePgDump())
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "--format=custom", "--compress=6", "--no-password",
                     $"--file={outputFile}", $"--host={connection.Host}", $"--port={connection.Port}",
                     $"--username={connection.Username}", $"--dbname={connection.Database}",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pg_dump could not be started.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"pg_dump exited with code {process.ExitCode}: {(await stderr).Trim()}");
        }
    }

    private string ResolvePgDump()
    {
        var configured = options.Value.Backup.PgDumpPath;
        if (Path.IsPathRooted(configured) && File.Exists(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Directory.Exists(Path.Combine(programFiles, "PostgreSQL"))
                ? Directory.GetDirectories(Path.Combine(programFiles, "PostgreSQL"))
                    .Select(d => Path.Combine(d, "bin", "pg_dump.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : null;
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return configured;
    }

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
    }
}

/// <summary>
/// Streaming authenticated encryption for large backups: fixed-size chunks, each AES-256-GCM with a random
/// nonce and the chunk index + final flag as associated data (detects reordering and truncation).
/// </summary>
public static class BackupEncryption
{
    private static readonly byte[] Magic = "HC360BK1"u8.ToArray();
    private const int ChunkSize = 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static async Task EncryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct)
    {
        await output.WriteAsync(Magic, ct);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, ChunkSize);
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(key, TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var currentLength = await ReadFullAsync(input, current, ct);
        long index = 0;

        while (true)
        {
            var nextLength = currentLength == ChunkSize ? await ReadFullAsync(input, next, ct) : 0;
            var isFinal = nextLength == 0;

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var tag = new byte[TagSize];
            var cipher = new byte[currentLength];
            aes.Encrypt(nonce, current.AsSpan(0, currentLength), cipher, tag, AssociatedData(index, isFinal));

            var lengthBytes = new byte[5];
            BinaryPrimitives.WriteInt32BigEndian(lengthBytes, currentLength);
            lengthBytes[4] = isFinal ? (byte)1 : (byte)0;
            await output.WriteAsync(nonce, ct);
            await output.WriteAsync(lengthBytes, ct);
            await output.WriteAsync(tag, ct);
            await output.WriteAsync(cipher, ct);

            if (isFinal)
            {
                break;
            }

            (current, next) = (next, current);
            currentLength = nextLength;
            index++;
        }
    }

    public static async Task DecryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct)
    {
        var magic = new byte[Magic.Length];
        if (await ReadFullAsync(input, magic, ct) != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new CryptographicException("Not an HC360 backup file.");
        }

        var header = new byte[4];
        await ReadFullAsync(input, header, ct);
        var chunkSize = BinaryPrimitives.ReadInt32BigEndian(header);
        if (chunkSize is <= 0 or > 64 * 1024 * 1024)
        {
            throw new CryptographicException("Corrupt backup header.");
        }

        using var aes = new AesGcm(key, TagSize);
        long index = 0;
        var nonce = new byte[NonceSize];
        var lengthBytes = new byte[5];
        var tag = new byte[TagSize];

        while (true)
        {
            if (await ReadFullAsync(input, nonce, ct) != NonceSize || await ReadFullAsync(input, lengthBytes, ct) != 5 || await ReadFullAsync(input, tag, ct) != TagSize)
            {
                throw new CryptographicException("Backup file is truncated.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            var isFinal = lengthBytes[4] == 1;
            if (length < 0 || length > chunkSize)
            {
                throw new CryptographicException("Corrupt backup chunk.");
            }

            var cipher = new byte[length];
            if (await ReadFullAsync(input, cipher, ct) != length)
            {
                throw new CryptographicException("Backup file is truncated.");
            }

            var plain = new byte[length];
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(index, isFinal));
            await output.WriteAsync(plain, ct);

            if (isFinal)
            {
                return;
            }

            index++;
        }
    }

    private static byte[] AssociatedData(long index, bool isFinal)
    {
        var data = new byte[9];
        BinaryPrimitives.WriteInt64BigEndian(data, index);
        data[8] = isFinal ? (byte)1 : (byte)0;
        return data;
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
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

        // The recovery key decrypts backups on replacement hardware. Export it once and store it offline (safe/locker).
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
