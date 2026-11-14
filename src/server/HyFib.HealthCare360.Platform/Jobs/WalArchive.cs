using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>What <see cref="WalArchive.DecryptFolderAsync"/> did with a folder of encrypted WAL files.</summary>
/// <param name="Decrypted">The files written, each checked against what it was encrypted from.</param>
/// <param name="OtherKeys">Files that open with another recovery key, by that key's id, and how many: run again with it.</param>
public sealed record WalDecryptResult(int Decrypted, IReadOnlyDictionary<string, int> OtherKeys);

/// <summary>
/// The archived WAL, encrypted (SRS REL-02). PostgreSQL archives each finished WAL file to a folder on the server's own disk
/// (Enable-HC360Replication.ps1 -WalArchive); HC360 encrypts it from there into <c>wal\&lt;system identifier&gt;</c> of the
/// backup folder, checks the encrypted file opens to the same bytes, and only then removes the plain one, so the WAL never
/// leaves the server unencrypted: the copies to the secondary directory, the disk taken off site, are of the encrypted files.
/// Each is a nightly backup's container (<see cref="BackupEncryption"/>) with the recovery key current at the time, holding
/// the WAL file compressed (most WAL files PostgreSQL closes after a quiet quarter of an hour are mostly empty), under its
/// own name, so a file renamed to stand in for another does not open as it.
/// </summary>
public static partial class WalArchive
{
    public const string FileExtension = ".hc360wal";

    private const string PartialExtension = ".partial";

    /// <summary>
    /// The folder an archive_command Enable-HC360Replication.ps1 wrote copies the WAL to (its Get-ArchiveCommand); null for
    /// any other command, whose archive HC360 leaves alone.
    /// </summary>
    public static string? ArchiveFolder(string archiveCommand) =>
        OwnArchiveCommand().Match(archiveCommand) is { Success: true } match ? match.Groups["folder"].Value : null;

    /// <summary>
    /// A file PostgreSQL archives: a WAL segment (<c>000000010000000000000042</c>), a timeline's history
    /// (<c>00000002.history</c>), a base backup's label (<c>….00000028.backup</c>) or the last, partial segment of a timeline
    /// a promotion ended (<c>….partial</c>). Anything else, such as the copy command's own temporary file, is not.
    /// </summary>
    public static bool IsArchivedName(string name) => ArchivedName().IsMatch(name);

    /// <summary>
    /// Where a WAL file's name puts it in the log, its timeline aside: the last 16 hexadecimal digits of a segment (and of
    /// the label and partial files named after one); null for a history file, which every restore across timelines needs.
    /// </summary>
    public static string? Position(string name) =>
        name.Length >= 24 && name[..24].All(char.IsAsciiHexDigitUpper) ? name[8..24] : null;

    /// <summary>
    /// The encrypted WAL files in <paramref name="names"/> that a restore from <paramref name="oldestBaseBackupWal"/> on no
    /// longer needs: those before it in the log, on any timeline. History files are kept.
    /// </summary>
    public static IEnumerable<string> Expired(IEnumerable<string> names, string oldestBaseBackupWal) =>
        Position(oldestBaseBackupWal) is { } oldest
            ? names.Where(name => Position(name) is { } position && string.CompareOrdinal(position, oldest) < 0)
            : [];

    /// <summary>
    /// Encrypts <paramref name="plainFile"/> into <paramref name="sealedFile"/> with <paramref name="key"/>, through a
    /// temporary name, and checks the encrypted file opens to the same bytes before it takes its real name.
    /// </summary>
    public static async Task SealAsync(string plainFile, string sealedFile, byte[] key, CancellationToken ct)
    {
        var name = Path.GetFileName(plainFile);
        var partial = sealedFile + PartialExtension;
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await using (var encryptor = BackupEncryption.CreateEncryptor(output, key))
                {
                    await using (var gzip = new GZipStream(encryptor, CompressionLevel.Fastest, leaveOpen: true))
                    await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
                    {
                        await using var source = OpenForReading(plainFile);
                        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = source }, ct);
                    }

                    await encryptor.CompleteAsync(ct);
                }

                output.Flush(flushToDisk: true);
            }

            if (!await SameAsync(partial, key, name, plainFile, ct))
            {
                throw new CryptographicException($"{name} was encrypted, but did not open to the same bytes.");
            }

            File.Move(partial, sealedFile);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Decrypts <paramref name="sealedFile"/> into <paramref name="output"/>, after checking it holds the WAL file
    /// <paramref name="expectedName"/> and nothing else, and reading it to its end so its last part is authenticated too.
    /// </summary>
    /// <exception cref="InvalidDataException">It holds another file, or more than one.</exception>
    public static async Task OpenAsync(string sealedFile, byte[] key, string expectedName, Stream output, CancellationToken ct)
    {
        await using var input = OpenForReading(sealedFile);
        await using var payload = await BackupEncryption.OpenAsync(input, key, ct);
        await using (var gzip = new GZipStream(payload, CompressionMode.Decompress, leaveOpen: true))
        {
            await using (var tar = new TarReader(gzip, leaveOpen: true))
            {
                var entry = await tar.GetNextEntryAsync(copyData: false, ct);
                if (entry is not { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile, DataStream: { } data } || entry.Name != expectedName)
                {
                    throw new InvalidDataException($"{Path.GetFileName(sealedFile)} does not hold the WAL file {expectedName}.");
                }

                await data.CopyToAsync(output, ct);
                if (await tar.GetNextEntryAsync(copyData: false, ct) is not null)
                {
                    throw new InvalidDataException($"{Path.GetFileName(sealedFile)} holds more than one file.");
                }
            }

            await gzip.CopyToAsync(Stream.Null, ct);
        }

        await payload.CopyToAsync(Stream.Null, ct);
    }

    /// <summary>
    /// Decrypts every encrypted WAL file in <paramref name="sealedFolder"/> into <paramref name="outputFolder"/>, under its
    /// WAL name, for PostgreSQL's restore_command to read during a restore to a point in time. Each is checked as it is
    /// written; one that opens with another recovery key is left, and counted under that key's id. A file already there is
    /// left as it is, so a second run with another key adds what the first could not open.
    /// </summary>
    public static async Task<WalDecryptResult> DecryptFolderAsync(string sealedFolder, byte[] key, string outputFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(outputFolder);
        var decrypted = 0;
        var otherKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sealedFolder, "*" + FileExtension).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file)[..^FileExtension.Length];
            var target = Path.Combine(outputFolder, name);
            if (!IsArchivedName(name) || File.Exists(target))
            {
                continue;
            }

            var temp = target + PartialExtension;
            try
            {
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await OpenAsync(file, key, name, output, ct);
                }

                File.Move(temp, target);
                decrypted++;
            }
            catch (BackupKeyMismatchException ex)
            {
                otherKeys[ex.BackupKeyId] = otherKeys.GetValueOrDefault(ex.BackupKeyId) + 1;
            }
            finally
            {
                File.Delete(temp);
            }
        }

        return new WalDecryptResult(decrypted, otherKeys);
    }

    /// <summary>Whether <paramref name="sealedFile"/> opens to exactly the bytes of <paramref name="plainFile"/>.</summary>
    internal static async Task<bool> SameAsync(string sealedFile, byte[] key, string name, string plainFile, CancellationToken ct)
    {
        using var opened = new MemoryStream();
        await OpenAsync(sealedFile, key, name, opened, ct);
        var plain = await File.ReadAllBytesAsync(plainFile, ct);
        return CryptographicOperations.FixedTimeEquals(opened.GetBuffer().AsSpan(0, (int)opened.Length), plain);
    }

    private static FileStream OpenForReading(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

    // As Enable-HC360Replication.ps1's Get-ArchiveCommand writes it, the folder the same each time it is named.
    [GeneratedRegex("""^if exist "(?<folder>[^"]+)\\%f" \(fc /b "%p" "\k<folder>\\%f" >nul\) else \(copy /b /y "%p" "\k<folder>\\%f\.tmp" >nul && move /y "\k<folder>\\%f\.tmp" "\k<folder>\\%f" >nul\)$""",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex OwnArchiveCommand();

    [GeneratedRegex("^(?:[0-9A-F]{24}(?:\\.partial|\\.[0-9A-F]{8}\\.backup)?|[0-9A-F]{8}\\.history)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ArchivedName();
}

/// <summary>
/// Every minute, on the server that runs the jobs, while PostgreSQL archives the WAL to the folder Enable-HC360Replication.ps1
/// set up: encrypts each archived WAL file into the backup folder (<see cref="WalArchive"/>) and removes the plain one; copies
/// the encrypted WAL and the base backups to the secondary directory, the disk taken off site or the NAS, by the system
/// identifier they belong to; and removes the encrypted WAL older than the oldest base backup kept, which no restore needs.
/// A WAL file archived again with other content than the one already encrypted is never encrypted over it: it stays in the
/// archive folder and System health says so. What happened is in <c>config\point-in-time-recovery.json</c>.
/// </summary>
internal sealed class WalArchiveService(
    IDbContextFactory<HospitalDbContext> contexts,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    RecoveryKeyStore recoveryKeys,
    TimeProvider time,
    ILogger<WalArchiveService> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (await ReadArchiveAsync(ct) is not { } archive)
        {
            return;
        }

        var status = PointInTimeRecovery.Load(directories);
        status = await SealAsync(archive.Folder, PointInTimeRecovery.WalFolder(directories.Backups, archive.Cluster.SystemIdentifier), status, ct);
        if (options.Value.Backup.SecondaryDirectory is { Length: > 0 } secondary)
        {
            status = Copy(secondary, archive.Cluster.SystemIdentifier, status);
        }

        RemoveExpired(archive.Cluster.SystemIdentifier, options.Value.Backup.SecondaryDirectory);

        // Only this job's own fields: the base backup job may have recorded its own meanwhile.
        PointInTimeRecovery.Update(directories, saved => saved with
        {
            WalSealedAt = status.WalSealedAt,
            WalSealed = status.WalSealed,
            WalFailedAt = status.WalFailedAt,
            WalError = status.WalError,
            WalConflict = status.WalConflict,
            CopiedAt = status.CopiedAt,
            CopyFailingSince = status.CopyFailingSince,
            CopyError = status.CopyError,
        });
    }

    /// <summary>
    /// Encrypts each WAL file in <paramref name="archiveFolder"/> into <paramref name="sealedFolder"/>, oldest name first.
    /// A file under its WAL name is whole: PostgreSQL's copy is written under a temporary name and renamed into place.
    /// </summary>
    internal async Task<PointInTimeRecoveryStatus> SealAsync(string archiveFolder, string sealedFolder, PointInTimeRecoveryStatus status, CancellationToken ct)
    {
        var pending = Directory.EnumerateFiles(archiveFolder)
            .Where(path => WalArchive.IsArchivedName(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (pending.Count == 0)
        {
            return status with { WalFailedAt = null, WalError = null, WalConflict = null };
        }

        Directory.CreateDirectory(sealedFolder);
        var key = recoveryKeys.Current();
        string? conflict = null;
        try
        {
            foreach (var plain in pending)
            {
                var name = Path.GetFileName(plain);
                var sealedFile = Path.Combine(sealedFolder, name + WalArchive.FileExtension);
                if (File.Exists(sealedFile))
                {
                    // PostgreSQL archives a file again after a crash before it recorded the first time: the same bytes are
                    // the same file. Other bytes are never put over the file kept.
                    if (!await SameOrOtherKeyAsync(sealedFile, key.Key, name, plain, ct))
                    {
                        conflict = name;
                        logger.LogError("PostgreSQL archived {File} again with other content than the copy already encrypted; both are kept", name);
                        continue;
                    }
                }
                else
                {
                    await WalArchive.SealAsync(plain, sealedFile, key.Key, ct);
                    status = status with { WalSealedAt = time.GetUtcNow(), WalSealed = name };
                }

                File.Delete(plain);
            }

            return status with { WalFailedAt = null, WalError = null, WalConflict = conflict };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
        {
            logger.LogError(ex, "Could not encrypt the archived WAL in {Folder}", archiveFolder);
            return status with { WalFailedAt = time.GetUtcNow(), WalError = ex.Message, WalConflict = conflict };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.Key);
        }
    }

    /// <summary>
    /// Copies the encrypted WAL and the base backups not yet in <paramref name="secondary"/> there, each under a temporary
    /// name first, so a copy cut short by an unplugged disk never passes for a whole one.
    /// </summary>
    internal PointInTimeRecoveryStatus Copy(string secondary, string systemIdentifier, PointInTimeRecoveryStatus status)
    {
        try
        {
            CopyMissing(PointInTimeRecovery.WalFolder(directories.Backups, systemIdentifier), PointInTimeRecovery.WalFolder(secondary, systemIdentifier),
                "*" + WalArchive.FileExtension);
            CopyMissing(PointInTimeRecovery.BaseBackupFolder(directories.Backups, systemIdentifier), PointInTimeRecovery.BaseBackupFolder(secondary, systemIdentifier),
                "*" + BaseBackupArchive.FileExtension);
            return status with { CopiedAt = time.GetUtcNow(), CopyFailingSince = null, CopyError = null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not copy the encrypted WAL and base backups to {Directory}", secondary);
            return status with { CopyFailingSince = status.CopyFailingSince ?? time.GetUtcNow(), CopyError = ex.Message };
        }
    }

    /// <summary>
    /// Removes the encrypted WAL no restore needs: older in the log than the WAL the oldest base backup kept starts from,
    /// here and in the secondary directory, and the base backups there past their days, as here. A share that does not let
    /// the server delete (DEPLOYMENT.md section 8) keeps them, for its own retention to remove.
    /// </summary>
    internal void RemoveExpired(string systemIdentifier, string? secondary)
    {
        var oldest = BaseBackupArchive.List(PointInTimeRecovery.BaseBackupFolder(directories.Backups, systemIdentifier)).LastOrDefault();
        if (oldest is null)
        {
            return;
        }

        var cutoff = time.GetUtcNow().AddDays(-Math.Max(options.Value.Backup.PointInTimeDays, 1));
        foreach (var root in new[] { directories.Backups, secondary }.OfType<string>().Where(r => r.Length > 0))
        {
            try
            {
                var wal = PointInTimeRecovery.WalFolder(root, systemIdentifier);
                if (Directory.Exists(wal))
                {
                    var names = Directory.EnumerateFiles(wal, "*" + WalArchive.FileExtension).Select(f => Path.GetFileName(f)[..^WalArchive.FileExtension.Length]).ToList();
                    foreach (var name in WalArchive.Expired(names, oldest.StartWalFile))
                    {
                        File.Delete(Path.Combine(wal, name + WalArchive.FileExtension));
                    }
                }

                if (!string.Equals(root, directories.Backups, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var old in BaseBackupArchive.Expired(BaseBackupArchive.List(PointInTimeRecovery.BaseBackupFolder(root, systemIdentifier)), cutoff))
                    {
                        File.Delete(old.Path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogInformation("Older encrypted WAL or base backups in {Folder} were not removed: {Reason}", root, ex.Message);
            }
        }
    }

    /// <summary>
    /// Copies each file of <paramref name="from"/> that <paramref name="to"/> does not hold whole. Straight to its own name,
    /// as the nightly backups' copies are: a share that lets the server add files but not delete them refuses a rename too.
    /// One cut short (the disk unplugged mid-copy) is shorter than the file, and is copied again.
    /// </summary>
    private static void CopyMissing(string from, string to, string pattern)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(from, pattern).Order(StringComparer.Ordinal))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length)
            {
                continue;
            }

            Directory.CreateDirectory(to);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Whether the encrypted file already kept holds the same bytes as the plain one. One made with an earlier recovery
    /// key cannot be opened with the current one: it was encrypted before the key changed, from the same file.
    /// </summary>
    private static async Task<bool> SameOrOtherKeyAsync(string sealedFile, byte[] key, string name, string plainFile, CancellationToken ct)
    {
        try
        {
            return await WalArchive.SameAsync(sealedFile, key, name, plainFile, ct);
        }
        catch (BackupKeyMismatchException)
        {
            return true;
        }
    }

    /// <summary>The archive folder of the archive_command Enable-HC360Replication.ps1 wrote, and the cluster; null when there is none here.</summary>
    private async Task<ArchiveFolder?> ReadArchiveAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            var connection = db.Database.GetDbConnection();
            var settings = await PointInTimeRecovery.ReadArchiveSettingsAsync(connection, ct);
            return settings.On && WalArchive.ArchiveFolder(settings.Command) is { } folder && Directory.Exists(folder)
                ? new ArchiveFolder(folder, await DatabaseCluster.ReadAsync(connection, ct))
                : null;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not read where PostgreSQL archives the WAL; it is encrypted at the next run");
            return null;
        }
    }

    private sealed record ArchiveFolder(string Folder, DatabaseCluster Cluster);
}

/// <summary>Encrypts and copies the archived WAL every minute (<see cref="WalArchiveService"/>).</summary>
[DisallowConcurrentExecution]
internal sealed class WalArchiveJob(WalArchiveService archive) : IJob
{
    public const string Schedule = "0 * * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await archive.RunAsync(cancellationToken);
}
