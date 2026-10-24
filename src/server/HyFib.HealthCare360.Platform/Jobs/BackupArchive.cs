using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>What a version 2 backup holds, written first so a restore can check everything that follows.</summary>
/// <param name="Format">The archive format (<see cref="BackupArchive.Format"/>).</param>
/// <param name="Kind">Scheduled, Manual or PreMigration.</param>
/// <param name="Migrations">The EF migrations applied to the database when it was dumped (its schema version).</param>
/// <param name="IncludesDocuments">False for the database-only backup taken before an update.</param>
/// <param name="KeyFingerprint">The fingerprint of the escrowed field master key (<see cref="FieldMasterKey.Fingerprint"/>).</param>
/// <param name="RingKeys">The ids of the keys rotations added to the ring (<see cref="FieldKeyRing"/>), escrowed too; none before the first rotation.</param>
/// <param name="CurrentKey">Which key of the ring was current, or null for the original key.</param>
/// <param name="AuditSealKey">The fingerprint of the escrowed audit seal key (<see cref="Auditing.AuditSealKey"/>); null in older backups.</param>
public sealed record BackupManifest(
    int Format,
    string Kind,
    DateTimeOffset CreatedAt,
    string ServerVersion,
    string Database,
    IReadOnlyList<string> Migrations,
    bool IncludesDocuments,
    int DocumentCount,
    long DocumentBytes,
    string KeyFingerprint,
    IReadOnlyList<string>? RingKeys = null,
    string? CurrentKey = null,
    string? AuditSealKey = null);

/// <summary>A stored document file, relative to the documents folder (forward slashes).</summary>
public sealed record BackupDocument(string RelativePath, long Length);

/// <summary>Where <see cref="BackupArchive.ReadAsync"/> writes what it reads; null reads and checks without writing.</summary>
public sealed record BackupReadTargets(string? DatabaseDumpPath = null, string? DocumentsFolder = null);

/// <summary>What an archive held, after every entry was read, authenticated and checked against the manifest.</summary>
public sealed record BackupContents(BackupManifest Manifest, byte[] MasterKey, int DocumentCount, long DocumentBytes)
{
    /// <summary>The keys rotations added to the ring, in the order the manifest lists them.</summary>
    public IReadOnlyList<byte[]> RingKeys { get; init; } = [];

    /// <summary>The audit seal key, when the backup escrows one.</summary>
    public byte[]? AuditSealKey { get; init; }
}

/// <summary>
/// The payload of an archive backup (DED-16): one tar archive with the manifest, an escrow copy of the field master key,
/// the PostgreSQL dump and, for full backups, the stored documents exactly as they are on disk (each already encrypted).
/// The archive itself is encrypted by <see cref="BackupEncryption"/> with the recovery key administrators keep offline
/// (<see cref="BackupRecoveryKey"/>), so one backup file and that key restore everything on replacement hardware.
/// </summary>
public static class BackupArchive
{
    public const int Format = 2;
    public const string ManifestEntry = "manifest.json";
    public const string KeyEntry = "keys/field-master.key";
    public const string RingKeyPrefix = "keys/" + FieldKeyRing.FolderName + "/";
    public const string AuditSealKeyEntry = "keys/" + Auditing.AuditSealKey.FileName;
    public const string DatabaseEntry = "database.dump";
    public const string DocumentsPrefix = "files/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The documents under <paramref name="documentsFolder"/>, in a stable order.</summary>
    public static IReadOnlyList<BackupDocument> ListDocuments(string documentsFolder)
    {
        if (!Directory.Exists(documentsFolder))
        {
            return [];
        }

        var root = Path.GetFullPath(documentsFolder);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new BackupDocument(Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length))
            .Where(d => IsSafeRelativePath(d.RelativePath))
            .OrderBy(d => d.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task WriteAsync(
        Stream output,
        BackupManifest manifest,
        byte[] masterKey,
        IReadOnlyList<byte[]> ringKeys,
        byte[]? auditSealKey,
        string databaseDumpPath,
        string documentsFolder,
        IReadOnlyList<BackupDocument> documents,
        CancellationToken ct)
    {
        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        await WriteEntryAsync(tar, ManifestEntry, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json)), ct);
        await WriteEntryAsync(tar, KeyEntry, new MemoryStream(masterKey, writable: false), ct);
        foreach (var key in ringKeys)
        {
            await WriteEntryAsync(tar, RingKeyPrefix + FieldMasterKey.Fingerprint(key) + ".key", new MemoryStream(key, writable: false), ct);
        }

        if (auditSealKey is not null)
        {
            await WriteEntryAsync(tar, AuditSealKeyEntry, new MemoryStream(auditSealKey, writable: false), ct);
        }
        await WriteEntryAsync(tar, DatabaseEntry, OpenForReading(databaseDumpPath), ct);
        foreach (var document in documents)
        {
            var source = OpenForReading(Path.Combine(documentsFolder, document.RelativePath));
            if (source.Length != document.Length)
            {
                await source.DisposeAsync();
                throw new InvalidOperationException($"Document {document.RelativePath} changed while it was being backed up.");
            }

            await WriteEntryAsync(tar, DocumentsPrefix + document.RelativePath, source, ct);
        }
    }

    /// <summary>
    /// Reads a whole archive: the manifest first, then the key (which must match the manifest's fingerprint), the database
    /// dump and the documents, whose count and size must match the manifest. Documents already present in
    /// <see cref="BackupReadTargets.DocumentsFolder"/> are left as they are (stored documents never change).
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is incomplete or does not match its manifest.</exception>
    public static async Task<BackupContents> ReadAsync(Stream payload, BackupReadTargets targets, CancellationToken ct)
    {
        BackupManifest? manifest = null;
        byte[]? masterKey = null;
        byte[]? auditSealKey = null;
        var ringKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var hasDatabase = false;
        var documentCount = 0;
        var documentBytes = 0L;

        await using (var tar = new TarReader(payload, leaveOpen: true))
        {
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new InvalidDataException($"Unexpected entry type {entry.EntryType} in the backup.");
                }

                if (manifest is null)
                {
                    manifest = entry.Name == ManifestEntry
                        ? await ParseManifestAsync(entry, ct)
                        : throw new InvalidDataException("The backup does not start with its manifest.");
                    continue;
                }

                switch (entry.Name)
                {
                    case KeyEntry when masterKey is null:
                        masterKey = await ReadSmallAsync(entry, FieldMasterKey.Length, ct);
                        if (masterKey.Length != FieldMasterKey.Length || FieldMasterKey.Fingerprint(masterKey) != manifest.KeyFingerprint)
                        {
                            throw new InvalidDataException("The encryption key in the backup does not match its manifest.");
                        }

                        break;

                    case var name when name.StartsWith(RingKeyPrefix, StringComparison.Ordinal):
                        var ringKey = await ReadSmallAsync(entry, FieldMasterKey.Length, ct);
                        var id = ringKey.Length == FieldMasterKey.Length ? FieldMasterKey.Fingerprint(ringKey) : null;
                        if (id is null || name != RingKeyPrefix + id + ".key" || manifest.RingKeys?.Contains(id) != true || !ringKeys.TryAdd(id, ringKey))
                        {
                            throw new InvalidDataException($"The encryption key {name} in the backup does not match its manifest.");
                        }

                        break;

                    case AuditSealKeyEntry when auditSealKey is null:
                        auditSealKey = await ReadSmallAsync(entry, FieldMasterKey.Length, ct);
                        if (auditSealKey.Length != FieldMasterKey.Length || FieldMasterKey.Fingerprint(auditSealKey) != manifest.AuditSealKey)
                        {
                            throw new InvalidDataException("The audit seal key in the backup does not match its manifest.");
                        }

                        break;

                    case DatabaseEntry when !hasDatabase:
                        hasDatabase = true;
                        await CopyEntryAsync(entry, targets.DatabaseDumpPath, overwrite: true, ct);
                        break;

                    case var name when name.StartsWith(DocumentsPrefix, StringComparison.Ordinal) && IsSafeRelativePath(name[DocumentsPrefix.Length..]):
                        documentCount++;
                        documentBytes += entry.Length;
                        var target = targets.DocumentsFolder is { } folder ? Path.Combine(folder, name[DocumentsPrefix.Length..]) : null;
                        await CopyEntryAsync(entry, target, overwrite: false, ct);
                        break;

                    default:
                        throw new InvalidDataException($"Unexpected entry '{entry.Name}' in the backup.");
                }
            }
        }

        // Read to the end, so the last encrypted chunk is authenticated too.
        await payload.CopyToAsync(Stream.Null, ct);

        if (manifest is null || masterKey is null || !hasDatabase)
        {
            throw new InvalidDataException("The backup is incomplete: the manifest, the encryption key or the database is missing.");
        }

        var listed = manifest.RingKeys ?? [];
        if (listed.Count != ringKeys.Count || (manifest.CurrentKey is { } current && current != manifest.KeyFingerprint && !ringKeys.ContainsKey(current)))
        {
            throw new InvalidDataException("The backup is incomplete: an encryption key its manifest lists is missing.");
        }

        if (manifest.AuditSealKey is not null && auditSealKey is null)
        {
            throw new InvalidDataException("The backup is incomplete: the audit seal key its manifest lists is missing.");
        }

        if (documentCount != manifest.DocumentCount || documentBytes != manifest.DocumentBytes)
        {
            throw new InvalidDataException($"The backup holds {documentCount} documents ({documentBytes} bytes); its manifest lists {manifest.DocumentCount} ({manifest.DocumentBytes} bytes).");
        }

        return new BackupContents(manifest, masterKey, documentCount, documentBytes)
        {
            RingKeys = listed.Select(id => ringKeys[id]).ToList(),
            AuditSealKey = auditSealKey,
        };
    }

    /// <summary>
    /// Document paths are the file store's own (<c>2026/09/&lt;id&gt;.bin</c>): relative, forward slashes, and only letters,
    /// digits, dots, dashes and underscores in each segment. Anything else (a drive, a root, "..") is refused.
    /// </summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 260)
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static FileStream OpenForReading(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task WriteEntryAsync(TarWriter tar, string name, Stream content, CancellationToken ct)
    {
        await using (content)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = content };
            await tar.WriteEntryAsync(entry, ct);
        }
    }

    /// <summary>Reads only the manifest (the first entry), to decide whether to go on before the rest is read.</summary>
    public static async Task<BackupManifest> ReadManifestAsync(Stream payload, CancellationToken ct)
    {
        await using var tar = new TarReader(payload, leaveOpen: true);
        return await tar.GetNextEntryAsync(copyData: false, ct) is { Name: ManifestEntry } entry
            ? await ParseManifestAsync(entry, ct)
            : throw new InvalidDataException("The backup does not start with its manifest.");
    }

    private static async Task<BackupManifest> ParseManifestAsync(TarEntry entry, CancellationToken ct)
    {
        var json = await ReadSmallAsync(entry, 1024 * 1024, ct);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, Json) ?? throw new InvalidDataException("The backup manifest is empty.");
        return manifest.Format == Format ? manifest : throw new InvalidDataException($"Backup format {manifest.Format} is not supported by this version of HC360.");
    }

    private static async Task<byte[]> ReadSmallAsync(TarEntry entry, int maxLength, CancellationToken ct)
    {
        if (entry.Length > maxLength || entry.DataStream is null)
        {
            throw new InvalidDataException($"The backup entry '{entry.Name}' is not valid.");
        }

        var buffer = new byte[entry.Length];
        await entry.DataStream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private static async Task CopyEntryAsync(TarEntry entry, string? target, bool overwrite, CancellationToken ct)
    {
        if (entry.DataStream is null)
        {
            return;
        }

        if (target is null || (!overwrite && File.Exists(target)))
        {
            await entry.DataStream.CopyToAsync(Stream.Null, ct);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await using var output = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await entry.DataStream.CopyToAsync(output, ct);
    }
}

/// <summary>The outcome of <see cref="BackupRestore.RestoreAsync"/>.</summary>
/// <param name="Manifest">Null for a version 1 backup, which holds the database only.</param>
/// <param name="PreviousKeyKeptAs">The file name the replaced field key was kept under, when a different key was installed.</param>
public sealed record BackupRestoreResult(int Version, BackupManifest? Manifest, int DocumentsRestored, int DocumentsAlreadyPresent, bool KeyInstalled, string? PreviousKeyKeptAs)
{
    /// <summary>How many keys rotations had added to the backup's key ring, installed beside the original key.</summary>
    public int RingKeysInstalled { get; init; }

    /// <summary>Whether the backup's audit seal key was installed (older backups have none).</summary>
    public bool AuditSealKeyInstalled { get; init; }

    /// <summary>
    /// Whether this machine now backs up with the recovery key the backup was opened with: it had no recovery key of its
    /// own, and the backup's was not the original key.
    /// </summary>
    public bool RecoveryKeyKept { get; init; }

    /// <summary>Why the recovery key could not be kept, when that failed after everything else was restored.</summary>
    public string? RecoveryKeyProblem { get; init; }
}

/// <summary>
/// Restores a backup on a Server Node, with the HC360 service stopped (the Server CLI's <c>backup restore</c>, run by
/// Restore-HC360.ps1). The whole archive is read and checked into a staging folder first; only then is the field master
/// key installed (wrapped for this machine) and are the documents moved into place, so a damaged backup changes nothing.
/// The database dump is written for pg_restore, which the script runs next.
/// </summary>
/// <summary>
/// Puts back what a backup holds besides its database, on the Server Node with the service stopped: the stored documents,
/// then the field encryption key. The whole archive is read and checked first, and the key comes last, so a failure never
/// leaves this machine with the backup's key but not its database. Restore-HC360.ps1 restores the database first, runs
/// this after it succeeds, and puts the previous database back if this fails.
/// </summary>
public static class BackupRestore
{
    /// <param name="databaseDumpPath">Where to write the database dump as well, or null to only check it.</param>
    public static async Task<BackupRestoreResult> RestoreAsync(
        string backupFile,
        byte[] recoveryKey,
        string? databaseDumpPath,
        DataDirectories directories,
        IDataProtectionProvider dataProtection,
        TimeProvider time,
        CancellationToken ct)
    {
        await using var input = new FileStream(backupFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var payload = await BackupEncryption.OpenAsync(input, recoveryKey, ct);
        if (payload.Version == BackupEncryption.LegacyVersion)
        {
            if (databaseDumpPath is null)
            {
                await payload.CopyToAsync(Stream.Null, ct);
            }
            else
            {
                await using var dump = new FileStream(databaseDumpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await payload.CopyToAsync(dump, ct);
            }

            return new BackupRestoreResult(payload.Version, null, 0, 0, KeyInstalled: false, null);
        }

        var staging = Path.Combine(directories.Temp, $"restore-{Guid.NewGuid():N}");
        try
        {
            var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(databaseDumpPath, staging), ct);
            try
            {
                // A version 3 backup names its recovery key in a header every chunk authenticates. A version 2 backup was
                // encrypted with the original recovery key, which comes from the key inside it.
                if (payload.Version == BackupEncryption.ArchiveVersion)
                {
                    CheckKey(contents.MasterKey, recoveryKey);
                }

                // Documents first: they are only ever added, and one the database does not refer to is harmless. The key
                // last: until it is installed, this machine keeps the key its current database was encrypted with.
                var (restored, present) = MoveDocuments(staging, directories.Files);

                // The keys rotations added only ever join the ring (one already here stays), then the original key, then
                // which key is current: data encrypted with any of them is readable once the database is back.
                var protector = FieldMasterKey.CreateProtector(dataProtection);
                foreach (var key in contents.RingKeys)
                {
                    FieldKeyRing.Add(directories, protector, key);
                }

                var keptAs = FieldMasterKey.Install(FieldMasterKey.PathIn(directories), protector, contents.MasterKey, time.GetUtcNow());

                // The seals of the restored audit trail were made with the backup's audit key; a different one here is kept
                // under a new name, as the field key is.
                if (contents.AuditSealKey is { } auditSealKey)
                {
                    FieldMasterKey.Install(Auditing.AuditSealKey.PathIn(directories), Auditing.AuditSealKey.CreateProtector(dataProtection), auditSealKey,
                        time.GetUtcNow());
                }

                // The backup decides which key encrypts new data, also when it is the original key (no current key in its
                // manifest): a key this machine had made current before is not used again, in case it was exposed.
                var current = contents.Manifest.CurrentKey;
                FieldKeyRing.SetCurrent(directories, current is null || current == contents.Manifest.KeyFingerprint ? null : current);

                // Backups made here from now on open with the key the hospital just used. Everything else is back by now,
                // so a failure here is reported rather than undoing the restore.
                bool recoveryKeyKept;
                string? recoveryKeyProblem = null;
                try
                {
                    recoveryKeyKept = RecoveryKeyStore.KeepAfterRestore(directories, dataProtection, recoveryKey, contents.MasterKey);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    recoveryKeyKept = false;
                    recoveryKeyProblem = ex.Message;
                }

                return new BackupRestoreResult(payload.Version, contents.Manifest, restored, present, KeyInstalled: true, keptAs)
                {
                    RingKeysInstalled = contents.RingKeys.Count,
                    AuditSealKeyInstalled = contents.AuditSealKey is not null,
                    RecoveryKeyKept = recoveryKeyKept,
                    RecoveryKeyProblem = recoveryKeyProblem,
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contents.MasterKey);
                foreach (var key in contents.RingKeys)
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                if (contents.AuditSealKey is { } sealKey)
                {
                    CryptographicOperations.ZeroMemory(sealKey);
                }
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// For a version 2 backup: it was encrypted with the original recovery key, derived from the escrowed master key. A
    /// mismatch means the archive's key is not the one its data was encrypted with, so it must not be installed.
    /// </summary>
    /// <exception cref="InvalidDataException">The key in the backup does not belong to the recovery key.</exception>
    public static void CheckKey(byte[] masterKey, byte[] recoveryKey)
    {
        if (!CryptographicOperations.FixedTimeEquals(BackupRecoveryKey.Original(masterKey), recoveryKey))
        {
            throw new InvalidDataException("The encryption key in the backup does not belong to this recovery key.");
        }
    }

    private static (int Restored, int AlreadyPresent) MoveDocuments(string staging, string documentsFolder)
    {
        if (!Directory.Exists(staging))
        {
            return (0, 0);
        }

        var restored = 0;
        var present = 0;
        foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(documentsFolder, Path.GetRelativePath(staging, source));
            if (File.Exists(target))
            {
                present++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            restored++;
        }

        return (restored, present);
    }
}
