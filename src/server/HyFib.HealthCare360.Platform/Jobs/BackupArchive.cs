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
/// <param name="DocumentIndexSha256">Format 3: the SHA-256 of the <c>documents.idx</c> entry (<see cref="BackupDocumentIndex"/>); null otherwise.</param>
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
    string? AuditSealKey = null,
    string? DocumentIndexSha256 = null);

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

    /// <summary>
    /// Format 3: the documents the backup needs, which are kept in the backup folder beside it (<see cref="BackupStore"/>)
    /// rather than in the archive. Empty for format 2, whose documents are inside.
    /// </summary>
    public IReadOnlyList<BackupIndexedDocument> Index { get; init; } = [];
}

/// <summary>
/// The payload of an archive backup (DED-16): one tar archive with the manifest, an escrow copy of the field master key,
/// and the PostgreSQL dump. The archive itself is encrypted by <see cref="BackupEncryption"/> with the recovery key
/// administrators keep offline (<see cref="BackupRecoveryKey"/>). For full backups, the stored documents (each already
/// encrypted with the field keys):
/// <list type="bullet">
/// <item>Format 3 (<see cref="Format"/>): an index of them (<see cref="BackupDocumentIndex"/>). Each document is kept
/// once in the backup folder beside the archive (<see cref="BackupStore"/>), so the folder and the recovery key restore
/// everything on replacement hardware.</item>
/// <item>Format 2 (<see cref="InlineDocumentsFormat"/>): the documents themselves, exactly as they are on disk. Still
/// read; one such file and its recovery key restore everything.</item>
/// </list>
/// </summary>
public static class BackupArchive
{
    /// <summary>The format written: documents listed in <see cref="DocumentIndexEntry"/>, kept in the backup folder.</summary>
    public const int Format = 3;

    /// <summary>The format before: documents inside the archive, under <see cref="DocumentsPrefix"/>. Still read.</summary>
    public const int InlineDocumentsFormat = 2;

    public const string DocumentIndexEntry = "documents.idx";
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

    /// <summary>
    /// Writes a format 2 archive, with the documents inside. Kept for the backups made before documents were kept in the
    /// backup folder; new backups are written with <see cref="WriteIndexedAsync"/>.
    /// </summary>
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
        ArgumentNullException.ThrowIfNull(manifest);
        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        var inline = manifest with { Format = InlineDocumentsFormat, DocumentIndexSha256 = null };
        await WriteEntryAsync(tar, ManifestEntry, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(inline, Json)), ct);
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
    /// Writes a format 3 archive: the manifest, the keys, the index of the documents (for a full backup) and the database
    /// dump. The documents themselves must be in the backup folder already. The manifest is completed here, so its format,
    /// document count, bytes and index hash always agree with the index written.
    /// </summary>
    /// <param name="documents">The documents the backup needs, or null for a backup without documents (before an update).</param>
    public static async Task<BackupManifest> WriteIndexedAsync(
        Stream output,
        BackupManifest manifest,
        byte[] masterKey,
        IReadOnlyList<byte[]> ringKeys,
        byte[]? auditSealKey,
        string databaseDumpPath,
        IReadOnlyList<BackupIndexedDocument>? documents,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var index = new MemoryStream();
        var indexSha256 = documents is null ? null : BackupDocumentIndex.Write(index, documents);
        var written = manifest with
        {
            Format = Format,
            IncludesDocuments = documents is not null,
            DocumentCount = documents?.Count ?? 0,
            DocumentBytes = documents?.Sum(d => d.Length) ?? 0,
            DocumentIndexSha256 = indexSha256,
        };

        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        await WriteEntryAsync(tar, ManifestEntry, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(written, Json)), ct);
        await WriteEntryAsync(tar, KeyEntry, new MemoryStream(masterKey, writable: false), ct);
        foreach (var key in ringKeys)
        {
            await WriteEntryAsync(tar, RingKeyPrefix + FieldMasterKey.Fingerprint(key) + ".key", new MemoryStream(key, writable: false), ct);
        }

        if (auditSealKey is not null)
        {
            await WriteEntryAsync(tar, AuditSealKeyEntry, new MemoryStream(auditSealKey, writable: false), ct);
        }

        if (documents is not null)
        {
            index.Position = 0;
            await WriteEntryAsync(tar, DocumentIndexEntry, index, ct);
        }

        await WriteEntryAsync(tar, DatabaseEntry, OpenForReading(databaseDumpPath), ct);
        return written;
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
        IReadOnlyList<BackupIndexedDocument>? index = null;

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

                    case DocumentIndexEntry when manifest.Format == Format && index is null && entry.DataStream is not null:
                        (index, var indexSha256) = await BackupDocumentIndex.ReadAsync(entry.DataStream, ct);
                        if (!string.Equals(indexSha256, manifest.DocumentIndexSha256, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("The backup's document index does not match its manifest.");
                        }

                        documentCount = index.Count;
                        documentBytes = index.Sum(d => d.Length);
                        break;

                    case var name when manifest.Format == InlineDocumentsFormat
                        && name.StartsWith(DocumentsPrefix, StringComparison.Ordinal) && IsSafeRelativePath(name[DocumentsPrefix.Length..]):
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

        // Format 3: a full backup has an index, and its manifest its hash; a backup without documents has neither.
        if (manifest.Format == Format
            && (manifest.IncludesDocuments != (manifest.DocumentIndexSha256 is not null) || manifest.IncludesDocuments != (index is not null)))
        {
            throw new InvalidDataException("The backup is incomplete: the document index its manifest lists is missing.");
        }

        if (documentCount != manifest.DocumentCount || documentBytes != manifest.DocumentBytes)
        {
            throw new InvalidDataException($"The backup holds {documentCount} documents ({documentBytes} bytes); its manifest lists {manifest.DocumentCount} ({manifest.DocumentBytes} bytes).");
        }

        return new BackupContents(manifest, masterKey, documentCount, documentBytes)
        {
            RingKeys = listed.Select(id => ringKeys[id]).ToList(),
            AuditSealKey = auditSealKey,
            Index = index ?? [],
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
        return manifest.Format is Format or InlineDocumentsFormat
            ? manifest
            : throw new InvalidDataException($"Backup format {manifest.Format} is not supported by this version of HC360.");
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
        CancellationToken ct,
        string? blobsFolder = null)
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
                // A format 3 backup's documents are in the backup folder beside it: each is copied into the staging folder
                // and checked there (its SHA-256, and that the backup holds the key it is encrypted with) before anything
                // on this machine changes, as the documents inside a format 2 backup are.
                await BackupBlobs.CopyAsync(blobsFolder ?? BackupBlobs.FolderOf(backupFile), contents, staging, ct);

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

/// <summary>
/// Finds the documents a format 3 backup needs (<see cref="BackupContents.Index"/>) in a backup folder, whatever its layout:
/// <c>blobs\ab\cd\&lt;id&gt;.blob</c> (<see cref="BackupStore"/>), the generations of an append-only destination
/// (<c>gen-*\blobs\…</c>), and the second copies written beside one cut short there (<c>&lt;id&gt;.1.blob</c>, …). A copy is
/// taken only when its length and SHA-256 are the index's, so where it was found does not matter, and a restore from any
/// one copy of the folder (the server's, a USB disk, a download of the bucket) gets the same bytes.
/// </summary>
public static class BackupBlobs
{
    /// <summary>The folder a backup's documents are looked for in by default: the folder the archive is in.</summary>
    public static string FolderOf(string archivePath) => Path.GetDirectoryName(Path.GetFullPath(archivePath))!;

    /// <summary>Where a copy of <paramref name="blobId"/> may be in <paramref name="folder"/>, the plain layout first.</summary>
    public static IEnumerable<string> Candidates(string folder, string blobId)
    {
        if (!BackupStore.IsBlobId(blobId))
        {
            yield break;
        }

        var roots = new List<string> { folder };
        if (Directory.Exists(folder))
        {
            roots.AddRange(Directory.EnumerateDirectories(folder, "gen-*").Order(StringComparer.Ordinal).Reverse());
        }

        foreach (var root in roots)
        {
            var directory = Path.Combine(root, BackupStore.BlobsFolder, blobId[..2], blobId[2..4]);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var first = Path.Combine(directory, blobId + BackupStore.BlobExtension);
            if (File.Exists(first))
            {
                yield return first;
            }

            foreach (var other in Directory.EnumerateFiles(directory, blobId + ".*" + BackupStore.BlobExtension).Order(StringComparer.Ordinal))
            {
                yield return other;
            }
        }
    }

    /// <summary>
    /// Copies every document of a format 3 backup into <paramref name="staging"/> at its storage path, each from the first
    /// copy whose length and SHA-256 match the index, checking that the backup holds the key it is encrypted with.
    /// </summary>
    /// <exception cref="InvalidDataException">A document has no good copy in the folder, or is encrypted with a key the backup does not hold.</exception>
    public static async Task CopyAsync(string folder, BackupContents contents, string staging, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var problems = new List<string>();
        foreach (var document in contents.Index)
        {
            var target = Path.Combine(staging, document.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var problem = await CopyOneAsync(folder, document, contents.Manifest, target, ct);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(Missing(folder, problems, contents.Index.Count));
        }
    }

    /// <summary>
    /// Checks every document of a format 3 backup in the folder, as <see cref="CopyAsync"/> would, without writing
    /// anything. Returns what is wrong, one line a document; empty when every document has a good copy.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(string folder, BackupContents contents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var problems = new List<string>();
        foreach (var document in contents.Index)
        {
            if (await CopyOneAsync(folder, document, contents.Manifest, null, ct) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    /// <summary>The message for documents without a good copy: how many, the first few, and where they were looked for.</summary>
    public static string Missing(string folder, IReadOnlyList<string> problems, int documents)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return $"{problems.Count} of the backup's {documents} documents have no good copy in {folder}: {string.Join("; ", problems.Take(3))}" +
            (problems.Count > 3 ? "; …" : "") +
            ". The documents are kept beside the archive in its backup folder: copy the whole folder, not only the .hc360bak file, " +
            "or name the folder with --blobs.";
    }

    private static async Task<string?> CopyOneAsync(string folder, BackupIndexedDocument document, BackupManifest manifest, string? target, CancellationToken ct)
    {
        var tried = false;
        foreach (var candidate in Candidates(folder, document.BlobId))
        {
            tried = true;
            if (new FileInfo(candidate).Length != document.Length)
            {
                continue;
            }

            var temp = target is null ? null : target + ".restoring";
            try
            {
                string? keyProblem = null;
                string sha256;
                await using (var source = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                await using (var output = temp is null ? null : new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 16];
                    var first = true;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        if (first)
                        {
                            keyProblem = KeyProblem(buffer.AsSpan(0, read), manifest, document);
                            first = false;
                        }

                        hash.AppendData(buffer, 0, read);
                        if (output is not null)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                    }

                    sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                }

                if (!string.Equals(sha256, document.BlobId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (keyProblem is not null)
                {
                    return keyProblem;
                }

                if (temp is not null)
                {
                    File.Move(temp, target!, overwrite: true);
                }

                return null;
            }
            finally
            {
                if (temp is not null)
                {
                    File.Delete(temp);
                }
            }
        }

        return tried ? $"{document.RelativePath} (every copy found is damaged)" : $"{document.RelativePath} (no copy)";
    }

    /// <summary>
    /// A stored document starts with the protector's version (1: the original key, which every backup holds; 2: then the
    /// 8-byte id of the key it is encrypted with). A document naming a key the backup does not hold would not be readable
    /// after the restore. Only that is refused: a header of another form is left to the file store to read.
    /// </summary>
    private static string? KeyProblem(ReadOnlySpan<byte> start, BackupManifest manifest, BackupIndexedDocument document)
    {
        if (start.Length >= 9 && start[0] == 2)
        {
            var keyId = Convert.ToHexString(start.Slice(1, 8));
            return manifest.RingKeys?.Contains(keyId, StringComparer.OrdinalIgnoreCase) == true
                || string.Equals(keyId, manifest.KeyFingerprint, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"{document.RelativePath} (encrypted with key {keyId}, which the backup does not hold)";
        }

        return null;
    }
}
