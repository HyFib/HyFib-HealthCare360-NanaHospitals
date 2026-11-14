using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>What a base backup holds, written first in its archive so a restore can check everything that follows.</summary>
/// <param name="Format">The archive format (<see cref="BaseBackupArchive.Format"/>).</param>
/// <param name="SystemIdentifier">The PostgreSQL cluster it is a copy of: only WAL archived from that cluster replays on it.</param>
/// <param name="Timeline">The timeline it was taken on.</param>
/// <param name="StartWalFile">
/// The WAL file PostgreSQL was writing just before the copy began: the copy needs no WAL from before it, so archived WAL
/// older than the oldest base backup kept is no longer needed.
/// </param>
/// <param name="Parts">The files pg_basebackup wrote (<c>base.tar.gz</c>, <c>pg_wal.tar.gz</c>, <c>backup_manifest</c>), with their sizes.</param>
public sealed record BaseBackupManifest(
    int Format,
    DateTimeOffset CreatedAt,
    string ServerVersion,
    string PostgresVersion,
    string Database,
    string SystemIdentifier,
    int Timeline,
    string StartWalFile,
    IReadOnlyList<BaseBackupPart> Parts);

/// <summary>A file pg_basebackup wrote, by its name, and its size.</summary>
public sealed record BaseBackupPart(string Name, long Length);

/// <summary>A base backup file, as its name describes it (<see cref="BaseBackupArchive.FileName"/>).</summary>
public sealed record BaseBackupFile(string Path, DateTimeOffset CreatedAt, string StartWalFile)
{
    public int Timeline => DatabaseCluster.TimelineOf(StartWalFile);
}

/// <summary>
/// The payload of a base backup (SRS REL-02): one tar archive with its manifest and the files pg_basebackup wrote, each as
/// it was written (<c>base.tar.gz</c> holds the data folder, <c>pg_wal.tar.gz</c> the WAL that makes it consistent). It is
/// encrypted as a nightly backup is (<see cref="BackupEncryption"/>), with the recovery key current when it was made, so
/// the recovery key that opens the nightly backups of the time opens it too, and nothing else does.
/// </summary>
public static class BaseBackupArchive
{
    public const int Format = 1;
    public const string FileExtension = ".hc360base";
    public const string ManifestEntry = "manifest.json";
    public const string PartPrefix = "pgdata/";

    private const string FilePrefix = "hc360-base-";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary><c>hc360-base-&lt;UTC time&gt;-&lt;start WAL file&gt;.hc360base</c>: its time and where its WAL starts, without opening it.</summary>
    public static string FileName(DateTimeOffset createdAt, string startWalFile) =>
        $"{FilePrefix}{createdAt.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}-{startWalFile}{FileExtension}";

    /// <summary>The base backup a file name describes; null for any other file.</summary>
    public static BaseBackupFile? Describe(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(FileExtension, StringComparison.Ordinal))
        {
            return null;
        }

        var middle = name[FilePrefix.Length..^FileExtension.Length];
        if (middle.Length != StampFormat.Length + 1 + 24 || middle[StampFormat.Length] != '-'
            || !DateTime.TryParseExact(middle[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdAt))
        {
            return null;
        }

        var wal = middle[(StampFormat.Length + 1)..];
        return wal.All(char.IsAsciiHexDigitUpper) ? new BaseBackupFile(path, new DateTimeOffset(createdAt, TimeSpan.Zero), wal) : null;
    }

    /// <summary>The base backups in <paramref name="folder"/>, newest first; none when there is no such folder.</summary>
    public static IReadOnlyList<BaseBackupFile> List(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*" + FileExtension).Select(Describe).OfType<BaseBackupFile>().OrderByDescending(f => f.CreatedAt)]
            : [];

    /// <summary>The files pg_basebackup wrote in <paramref name="folder"/>, by name.</summary>
    public static IReadOnlyList<BaseBackupPart> ListParts(string folder) =>
        [.. Directory.EnumerateFiles(folder)
            .Select(path => new BaseBackupPart(System.IO.Path.GetFileName(path), new FileInfo(path).Length))
            .OrderBy(p => p.Name, StringComparer.Ordinal)];

    /// <summary>Writes the manifest, then each part from <paramref name="partsFolder"/>, as the manifest lists them.</summary>
    public static async Task WriteAsync(Stream output, BaseBackupManifest manifest, string partsFolder, CancellationToken ct)
    {
        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        await WriteEntryAsync(tar, ManifestEntry, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json)), ct);
        foreach (var part in manifest.Parts)
        {
            if (!IsSafePartName(part.Name))
            {
                throw new InvalidOperationException($"pg_basebackup wrote a file HC360 does not expect: {part.Name}.");
            }

            var source = new FileStream(System.IO.Path.Combine(partsFolder, part.Name), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != part.Length)
            {
                await source.DisposeAsync();
                throw new InvalidOperationException($"{part.Name} changed while the base backup was being written.");
            }

            await WriteEntryAsync(tar, PartPrefix + part.Name, source, ct);
        }
    }

    /// <summary>
    /// Reads a whole archive: the manifest first, then every part it lists, each of the size it says, and nothing else.
    /// With <paramref name="outputFolder"/>, each part is written there under its own name, for tar to extract (see
    /// docs/deployment/DEPLOYMENT.md, "A restore to a point in time").
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is incomplete or does not match its manifest.</exception>
    public static async Task<BaseBackupManifest> ReadAsync(Stream payload, string? outputFolder, CancellationToken ct)
    {
        BaseBackupManifest? manifest = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using (var tar = new TarReader(payload, leaveOpen: true))
        {
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new InvalidDataException($"Unexpected entry type {entry.EntryType} in the base backup.");
                }

                if (manifest is null)
                {
                    manifest = entry.Name == ManifestEntry
                        ? await ParseManifestAsync(entry, ct)
                        : throw new InvalidDataException("The base backup does not start with its manifest.");
                    continue;
                }

                var name = entry.Name.StartsWith(PartPrefix, StringComparison.Ordinal) ? entry.Name[PartPrefix.Length..] : null;
                var part = name is null ? null : manifest.Parts.FirstOrDefault(p => p.Name == name);
                if (part is null || !IsSafePartName(part.Name) || !seen.Add(part.Name) || entry.Length != part.Length)
                {
                    throw new InvalidDataException($"The entry '{entry.Name}' does not match the base backup's manifest.");
                }

                if (entry.DataStream is null)
                {
                    continue;
                }

                if (outputFolder is null)
                {
                    await entry.DataStream.CopyToAsync(Stream.Null, ct);
                    continue;
                }

                Directory.CreateDirectory(outputFolder);
                await using var output = new FileStream(System.IO.Path.Combine(outputFolder, part.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await entry.DataStream.CopyToAsync(output, ct);
            }
        }

        // Read to the end, so the last encrypted chunk is authenticated too.
        await payload.CopyToAsync(Stream.Null, ct);
        if (manifest is null)
        {
            throw new InvalidDataException("The base backup is empty.");
        }

        if (manifest.Parts.Any(p => !seen.Contains(p.Name)) || !manifest.Parts.Any(p => p.Name.StartsWith("base.tar", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The base backup is incomplete: a file its manifest lists is missing.");
        }

        return manifest;
    }

    /// <summary>
    /// Decrypts the base backup <paramref name="file"/> into <paramref name="outputFolder"/>, new or empty, after checking it
    /// in full as it goes; if anything is wrong with it, nothing it held is left there.
    /// </summary>
    /// <exception cref="IOException">The folder is not empty.</exception>
    public static async Task<BaseBackupManifest> DecryptToFolderAsync(string file, byte[] key, string outputFolder, CancellationToken ct)
    {
        if (Directory.Exists(outputFolder) && Directory.EnumerateFileSystemEntries(outputFolder).Any())
        {
            throw new IOException($"{outputFolder} is not empty: give a new folder for the base backup's files.");
        }

        Directory.CreateDirectory(outputFolder);
        try
        {
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using var payload = await BackupEncryption.OpenAsync(input, key, ct);
            return await ReadAsync(payload, outputFolder, ct);
        }
        catch
        {
            foreach (var written in Directory.EnumerateFiles(outputFolder))
            {
                File.Delete(written);
            }

            throw;
        }
    }

    /// <summary>A part is one of pg_basebackup's own files: letters, digits, dots, dashes and underscores, no folder.</summary>
    internal static bool IsSafePartName(string name) =>
        name.Length is > 0 and <= 64 && name is not ("." or "..") && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static async Task WriteEntryAsync(TarWriter tar, string name, Stream content, CancellationToken ct)
    {
        await using (content)
        {
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = content }, ct);
        }
    }

    private static async Task<BaseBackupManifest> ParseManifestAsync(TarEntry entry, CancellationToken ct)
    {
        if (entry.DataStream is null || entry.Length > 1024 * 1024)
        {
            throw new InvalidDataException("The base backup's manifest is not valid.");
        }

        var json = new byte[entry.Length];
        await entry.DataStream.ReadExactlyAsync(json, ct);
        var manifest = JsonSerializer.Deserialize<BaseBackupManifest>(json, Json) ?? throw new InvalidDataException("The base backup's manifest is empty.");
        return manifest.Format == Format
            ? manifest
            : throw new InvalidDataException($"Base backup format {manifest.Format} is not supported by this version of HC360.");
    }
}

/// <summary>When the next base backup is due.</summary>
public static class BaseBackupSchedule
{
    /// <summary>Every Sunday at 03:30 in the hospital's time zone, after the nightly backup and before the day starts.</summary>
    public const string Weekly = "0 30 3 ? * SUN";

    /// <summary>After a base backup fails, the next try waits this long, rather than load the disk every quarter of an hour.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    /// <summary>
    /// Whether a base backup is due at <paramref name="now"/>: there is none of the cluster's current timeline
    /// (archiving was just turned on, or a standby was promoted and its WAL starts a new timeline), or the weekly time has
    /// come since the newest. Not within <see cref="RetryAfter"/> of a failed attempt.
    /// </summary>
    public static bool Due(BaseBackupFile? newest, int timeline, DateTimeOffset? lastFailureAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (lastFailureAt is { } failed && now - failed < RetryAfter)
        {
            return false;
        }

        if (newest is null || newest.Timeline != timeline)
        {
            return true;
        }

        var trigger = (ICronTrigger)TriggerBuilder.Create().WithCronSchedule(Weekly, cron => cron.InTimeZone(zone)).StartAt(newest.CreatedAt).Build();
        return trigger.GetFireTimeAfter(newest.CreatedAt) is { } due && due <= now;
    }
}

/// <summary>A base backup that was made, checked and kept.</summary>
public sealed record BaseBackupResult(string FileName, long SizeBytes, string Sha256, string RecoveryKeyId, string SystemIdentifier, int Timeline);

/// <summary>Copies the PostgreSQL cluster into a folder, as pg_basebackup does: the job's one call to PostgreSQL's tools.</summary>
internal interface IBaseBackupTool
{
    /// <summary>
    /// Writes a copy of the whole cluster into <paramref name="targetFolder"/>, which must be empty, as tar files with the WAL
    /// that makes the copy consistent.
    /// </summary>
    Task CopyClusterAsync(string targetFolder, string label, CancellationToken ct);
}

/// <summary>
/// pg_basebackup, signing in as <see cref="Role"/>: an account that may only stream the database (REPLICATION), which
/// Enable-HC360Replication.ps1 -WalArchive makes, and which takes the HC360 service's Windows account from this machine with
/// no password (SSPI, as the service's own database account). HC360's own accounts may not replicate.
/// </summary>
internal sealed class PgBaseBackupTool(IConfiguration configuration, IOptions<PlatformOptions> options) : IBaseBackupTool
{
    /// <summary>The account Enable-HC360Replication.ps1 makes for HC360's base backups.</summary>
    public const string Role = "hc360_basebackup";

    private static readonly TimeSpan Timeout = TimeSpan.FromHours(6);

    public async Task CopyClusterAsync(string targetFolder, string label, CancellationToken ct)
    {
        var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Hospital"));
        var executable = Resolve();
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments(targetFolder, label, connection.Host ?? "localhost", connection.Port))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // No password: the account takes the service's Windows account. Nothing from the environment stands in for it.
        startInfo.Environment["PGPASSWORD"] = string.Empty;

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("pg_basebackup could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"pg_basebackup was not found ({executable}): install it with PostgreSQL, beside pg_dump.", ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await stdout;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"pg_basebackup exited with code {process.ExitCode}: {(await stderr).Trim()}");
            }
        }
    }

    /// <summary>
    /// Tar files, compressed, with the WAL streamed beside the copy through a temporary slot, so the copy is consistent on
    /// its own however long it takes; a checkpoint at once rather than at PostgreSQL's next one; never a password prompt.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string targetFolder, string label, string host, int port) =>
    [
        $"--pgdata={targetFolder}", "--format=tar", "--gzip", "--wal-method=stream", "--checkpoint=fast", $"--label={label}",
        "--no-password", $"--host={host}", $"--port={port.ToString(CultureInfo.InvariantCulture)}", $"--username={Role}",
    ];

    /// <summary>The pg_basebackup beside the configured pg_dump, else the newest PostgreSQL's on this machine, else the one on the path.</summary>
    private string Resolve()
    {
        var name = OperatingSystem.IsWindows() ? "pg_basebackup.exe" : "pg_basebackup";
        if (Path.IsPathRooted(options.Value.Backup.PgDumpPath) && Path.GetDirectoryName(options.Value.Backup.PgDumpPath) is { } folder
            && File.Exists(Path.Combine(folder, name)))
        {
            return Path.Combine(folder, name);
        }

        var postgres = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (OperatingSystem.IsWindows() && Directory.Exists(postgres))
        {
            var newest = Directory.GetDirectories(postgres)
                .Select(d => (Version: PostgresTools.InstallVersion(Path.GetFileName(d)), Tool: Path.Combine(d, "bin", name)))
                .Where(t => File.Exists(t.Tool))
                .OrderByDescending(t => t.Version)
                .Select(t => t.Tool)
                .FirstOrDefault();
            if (newest is not null)
            {
                return newest;
            }
        }

        return "pg_basebackup";
    }
}

/// <summary>
/// The weekly base backup (SRS REL-02), while PostgreSQL archives the WAL (Enable-HC360Replication.ps1 -WalArchive): with
/// it and the WAL archived since, the database can be restored to any moment, not only to last night's backup. Each is
/// pg_basebackup's copy of the cluster, read back and checked once encrypted, kept in <c>base\&lt;system identifier&gt;</c>
/// of the backup folder for the days nightly backups are kept, the newest always. One is taken at once when there is none
/// of the current timeline: archiving was just turned on, or a promoted standby now runs on a new timeline. The plain copy
/// pg_basebackup writes stays in the temp folder on the server's own disk only until it is encrypted, as the nightly
/// backup's dump does.
/// </summary>
internal sealed class BaseBackupService(
    IDbContextFactory<HospitalDbContext> contexts,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    RecoveryKeyStore recoveryKeys,
    IBaseBackupTool tool,
    INotificationService notifications,
    TimeProvider time,
    ILogger<BaseBackupService> logger)
{
    public const string FailedTitle = "Base backup failed";

    private const string PartialExtension = ".partial";

    /// <summary>The prefix of pg_basebackup's working folders in the temp folder, removed when a server stopped mid-copy left one.</summary>
    internal const string WorkFolderPrefix = "basebackup-";

    private static readonly string ServerVersion =
        typeof(BaseBackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Takes a base backup when PostgreSQL archives the WAL and one is due (<see cref="BaseBackupSchedule.Due"/>).</summary>
    /// <returns>The base backup taken; null when none was due or it failed (the failure is recorded and told).</returns>
    public async Task<BaseBackupResult?> RunIfDueAsync(CancellationToken ct)
    {
        var database = await ReadDatabaseAsync(ct);
        if (database is null)
        {
            return null;
        }

        var status = PointInTimeRecovery.Load(directories);
        var newest = BaseBackupArchive.List(PointInTimeRecovery.BaseBackupFolder(directories.Backups, database.Cluster.SystemIdentifier)).FirstOrDefault();
        var zone = PlatformServiceCollectionExtensions.ResolveTimeZone(options.Value.TimeZone);
        return BaseBackupSchedule.Due(newest, database.Cluster.Timeline, status.BaseBackupFailedAt, time.GetUtcNow(), zone)
            ? await RunAsync(database, ct)
            : null;
    }

    private async Task<BaseBackupResult?> RunAsync(ArchivedDatabase database, CancellationToken ct)
    {
        try
        {
            var result = await CreateAsync(database, ct);
            PointInTimeRecovery.Save(directories, PointInTimeRecovery.Load(directories) with
            {
                BaseBackupAt = time.GetUtcNow(),
                BaseBackupFile = result.FileName,
                BaseBackupFailedAt = null,
                BaseBackupError = null,
            });
            logger.LogInformation("Base backup {File} made and checked ({Size:N0} bytes, timeline {Timeline})", result.FileName, result.SizeBytes, result.Timeline);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            logger.LogError(ex, "Base backup failed");
            PointInTimeRecovery.Save(directories, PointInTimeRecovery.Load(directories) with { BaseBackupFailedAt = time.GetUtcNow(), BaseBackupError = reason });
            try
            {
                await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, FailedTitle,
                    $"{reason} The nightly backups are not affected; a restore to a point in time needs a base backup. HC360 tries again in 6 hours.",
                    "/admin/system", "system", "warning", CancellationToken.None);
            }
            catch (Exception notifyFailure) when (notifyFailure is not OperationCanceledException)
            {
                logger.LogWarning(notifyFailure, "Could not tell the administrators that the base backup failed");
            }

            return null;
        }
    }

    internal async Task<BaseBackupResult> CreateAsync(ArchivedDatabase database, CancellationToken ct)
    {
        var startedAt = time.GetUtcNow();
        Directory.CreateDirectory(directories.Temp);
        RemoveLeftovers();
        var folder = PointInTimeRecovery.BaseBackupFolder(directories.Backups, database.Cluster.SystemIdentifier);
        Directory.CreateDirectory(folder);
        EnsureRoom(database.SizeBytes);

        var fileName = BaseBackupArchive.FileName(startedAt, database.Cluster.WalFile);
        var target = Path.Combine(folder, fileName);
        var partial = target + PartialExtension;
        var work = Path.Combine(directories.Temp, WorkFolderPrefix + Guid.NewGuid().ToString("N"));
        var recoveryKey = recoveryKeys.Current();
        try
        {
            Directory.CreateDirectory(work);
            await tool.CopyClusterAsync(work, string.Create(CultureInfo.InvariantCulture, $"HC360 base backup {startedAt:yyyy-MM-dd HH:mm} UTC"), ct);
            var parts = BaseBackupArchive.ListParts(work);
            if (!parts.Any(p => p.Name.StartsWith("base.tar", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("pg_basebackup wrote no copy of the data folder (base.tar).");
            }

            var manifest = new BaseBackupManifest(BaseBackupArchive.Format, startedAt, ServerVersion, database.PostgresVersion, database.Name,
                database.Cluster.SystemIdentifier, database.Cluster.Timeline, database.Cluster.WalFile, parts);
            string sha256;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await using var encryptor = BackupEncryption.CreateEncryptor(file, recoveryKey.Key);
                await BaseBackupArchive.WriteAsync(encryptor, manifest, work, ct);
                await encryptor.CompleteAsync(ct);
                sha256 = encryptor.Sha256!;
            }

            // The plain copy goes as soon as the encrypted one is written; the check reads the encrypted one back.
            Directory.Delete(work, recursive: true);
            await VerifyAsync(partial, recoveryKey.Key, ct);
            File.Move(partial, target);
            RemoveOld(folder);
            return new BaseBackupResult(fileName, new FileInfo(target).Length, sha256, recoveryKey.KeyId, database.Cluster.SystemIdentifier, database.Cluster.Timeline);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey.Key);
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            File.Delete(partial);
        }
    }

    /// <summary>Reads the whole file back as a restore would: every chunk authenticated, every part as the manifest lists it.</summary>
    private static async Task VerifyAsync(string file, byte[] key, CancellationToken ct)
    {
        try
        {
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using var payload = await BackupEncryption.OpenAsync(input, key, ct);
            await BaseBackupArchive.ReadAsync(payload, outputFolder: null, ct);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            throw new InvalidOperationException("The base backup was written but could not be read back, so it was discarded: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// The base backups past the days nightly backups are kept, all but the newest, which a restore to a point in time
    /// always needs. Left behind half-written files go after a day.
    /// </summary>
    internal void RemoveOld(string folder)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(options.Value.Backup.RetentionDays, 1));
        foreach (var old in BaseBackupArchive.List(folder).Skip(1).Where(f => f.CreatedAt < cutoff))
        {
            File.Delete(old.Path);
        }

        var stale = time.GetUtcNow().UtcDateTime.AddDays(-1);
        foreach (var leftover in Directory.EnumerateFiles(folder, "*" + BaseBackupArchive.FileExtension + PartialExtension))
        {
            if (File.GetLastWriteTimeUtc(leftover) < stale)
            {
                File.Delete(leftover);
            }
        }
    }

    /// <summary>
    /// A server stopped in the middle of a base backup leaves pg_basebackup's plain copy in the temp folder: it is removed
    /// before the next one starts. One still being written is never this old.
    /// </summary>
    private void RemoveLeftovers()
    {
        var cutoff = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(12);
        foreach (var leftover in new DirectoryInfo(directories.Temp).EnumerateDirectories(WorkFolderPrefix + "*").Where(d => d.LastWriteTimeUtc < cutoff))
        {
            try
            {
                leftover.Delete(recursive: true);
                logger.LogWarning("Removed {Folder}, the copy of the database an interrupted base backup left behind", leftover.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {Folder}, left behind by an interrupted base backup", leftover.Name);
            }
        }
    }

    /// <summary>
    /// The copy and its encrypted archive each take about the database's size (less, compressed): room for both and 1 GB to
    /// spare, or the base backup is refused before it fills the disk the database is on.
    /// </summary>
    private void EnsureRoom(long databaseBytes)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directories.Temp))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        var needed = (2 * databaseBytes) + (1L << 30);
        if (free < needed)
        {
            throw new InvalidOperationException(
                $"Not enough free disk space for a base backup in {directories.Temp}: {free / (1 << 20):N0} MB free, about {needed / (1 << 20):N0} MB needed. Free up space.");
        }
    }

    /// <summary>The database while PostgreSQL archives its WAL; null when it does not, or cannot be asked.</summary>
    private async Task<ArchivedDatabase?> ReadDatabaseAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            var connection = db.Database.GetDbConnection();
            if (!(await PointInTimeRecovery.ReadArchiveSettingsAsync(connection, ct)).On)
            {
                return null;
            }

            var cluster = await DatabaseCluster.ReadAsync(connection, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_setting('server_version'), current_database(), pg_database_size(current_database())";
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new ArchivedDatabase(cluster, reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not read whether PostgreSQL archives the WAL; no base backup this time");
            return null;
        }
    }
}

/// <summary>The hospital database while its WAL is archived: its cluster, PostgreSQL's version, its name and size.</summary>
internal sealed record ArchivedDatabase(DatabaseCluster Cluster, string PostgresVersion, string Name, long SizeBytes);

/// <summary>Checks every quarter of an hour whether a base backup is due (<see cref="BaseBackupService.RunIfDueAsync"/>).</summary>
[DisallowConcurrentExecution]
internal sealed class BaseBackupJob(BaseBackupService backups) : IJob
{
    public const string CheckSchedule = "0 7/15 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await backups.RunIfDueAsync(cancellationToken);
}
