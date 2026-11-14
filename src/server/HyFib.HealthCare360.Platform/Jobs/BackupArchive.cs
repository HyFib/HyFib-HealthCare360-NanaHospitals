using System.Buffers;
using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// <param name="RowCounts">
/// The rows of the key tables in the dump, counted in the very snapshot pg_dump dumped (<see cref="BackupRowCounts"/>), which
/// a test restore checks the restored database against; null in older backups.
/// </param>
/// <param name="DocumentHashes">
/// Whether the archive records the SHA-256 of each document as it was copied (<see cref="BackupArchive.DocumentHashesEntry"/>,
/// after the documents); false in older backups. <see cref="BackupArchive.WriteAsync"/> sets it.
/// </param>
/// <param name="ErasureLedger">
/// The SHA-256 of the erasure ledger the archive carries (<see cref="BackupArchive.ErasureLedgerEntry"/>); null in older
/// backups. <see cref="BackupArchive.WriteAsync"/> sets it.
/// </param>
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
    IReadOnlyDictionary<string, long>? RowCounts = null,
    bool DocumentHashes = false,
    string? ErasureLedger = null);

/// <summary>A stored document file, relative to the documents folder (forward slashes).</summary>
public sealed record BackupDocument(string RelativePath, long Length);

/// <summary>
/// What a backup carries besides the database, the keys and the documents: what is kept outside the database and a restore
/// on new hardware needs. Each is left out when null.
/// </summary>
/// <param name="ErasureLedger">The erasure ledger's lines, as <see cref="ErasureLedgerFiles.ReadForBackup"/> reads them.</param>
public sealed record BackupExtras(byte[]? ErasureLedger = null);

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
    /// The SHA-256 (lower-case hex) of each document the archive holds, by its path relative to the documents folder: the
    /// hash the archive records, which its copy matched, or, in an older backup that records none, the hash of its copy.
    /// </summary>
    public IReadOnlyDictionary<string, string> DocumentHashes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The erasure ledger the backup carries, as the server had it when the backup was made; null in older backups.</summary>
    public byte[]? ErasureLedger { get; init; }
}

/// <summary>
/// The payload of an archive backup (DED-16): one tar archive with the manifest, an escrow copy of the field master key,
/// the erasure ledger, the PostgreSQL dump and, for full backups, the stored documents exactly as they are on disk (each
/// already encrypted), followed by the SHA-256 of each as it was copied.
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

    /// <summary>
    /// The SHA-256 of every document, written after the documents as each was copied: one line each, as sha256sum writes
    /// them (<c>&lt;hash&gt;  files/2026/09/&lt;id&gt;.bin</c>), so the archive's own record of every document can be checked
    /// with ordinary tools once it is decrypted.
    /// </summary>
    public const string DocumentHashesEntry = "documents.sha256";

    /// <summary>
    /// The erasure ledger (DPDP Act 2023 s8(7)), as the server had it once the database was dumped: inside the encryption,
    /// so every copy of the backup, the off-site one included, carries it to wherever the backup is restored.
    /// </summary>
    public const string ErasureLedgerEntry = "privacy/" + ErasureLedgerFiles.FileName;

    /// <summary>The largest erasure ledger a backup carries: some 300,000 erasures, a line each.</summary>
    private const int MaxErasureLedgerLength = 128 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

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

    public static Task WriteAsync(
        Stream output,
        BackupManifest manifest,
        byte[] masterKey,
        IReadOnlyList<byte[]> ringKeys,
        byte[]? auditSealKey,
        string databaseDumpPath,
        string documentsFolder,
        IReadOnlyList<BackupDocument> documents,
        CancellationToken ct) =>
        WriteAsync(output, manifest, masterKey, ringKeys, auditSealKey, databaseDumpPath, documentsFolder, documents, new BackupExtras(), ct);

    /// <summary>
    /// Writes a whole archive, with <paramref name="extras"/> beside the keys. The manifest's record of what follows it (the
    /// erasure ledger's hash, that the documents' hashes are recorded) is made here, from what is written.
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
        BackupExtras extras,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extras);
        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);

        // The manifest says what follows, so a restore can tell an archive cut short from an older one.
        manifest = manifest with
        {
            DocumentHashes = true,
            ErasureLedger = extras.ErasureLedger is { } ledger ? Convert.ToHexStringLower(SHA256.HashData(ledger)) : null,
        };
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

        if (extras.ErasureLedger is { } erasures)
        {
            await WriteEntryAsync(tar, ErasureLedgerEntry, new MemoryStream(erasures, writable: false), ct);
        }

        await WriteEntryAsync(tar, DatabaseEntry, OpenForReading(databaseDumpPath), ct);

        // Each document is hashed as it is copied, so its hash is that of the very bytes in the archive, read once.
        var hashes = new StringBuilder();
        foreach (var document in documents)
        {
            var source = OpenForReading(Path.Combine(documentsFolder, document.RelativePath));
            if (source.Length != document.Length)
            {
                await source.DisposeAsync();
                throw new InvalidOperationException($"Document {document.RelativePath} changed while it was being backed up.");
            }

            var hashing = new HashingReadStream(source);
            await WriteEntryAsync(tar, DocumentsPrefix + document.RelativePath, hashing, ct);
            hashes.Append(hashing.Sha256).Append("  ").Append(DocumentsPrefix).Append(document.RelativePath).Append('\n');
        }

        await WriteEntryAsync(tar, DocumentHashesEntry, new MemoryStream(Encoding.UTF8.GetBytes(hashes.ToString()), writable: false), ct);
    }

    /// <summary>
    /// Reads a whole archive: the manifest first, then the key (which must match the manifest's fingerprint), the database
    /// dump and the documents, whose count and size must match the manifest, and whose hashes must match those the archive
    /// records. Documents already present in <see cref="BackupReadTargets.DocumentsFolder"/> are left as they are; a restore
    /// reads into an empty folder and compares them itself (<see cref="BackupRestore"/>).
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
        var documentHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string>? recordedHashes = null;
        byte[]? erasureLedger = null;

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

                    case ErasureLedgerEntry when erasureLedger is null:
                        // An empty ledger (nothing erased yet) is an entry without data.
                        erasureLedger = entry.Length == 0 ? [] : await ReadSmallAsync(entry, MaxErasureLedgerLength, ct);
                        if (manifest.ErasureLedger is null
                            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(erasureLedger)), manifest.ErasureLedger, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("The erasure ledger in the backup does not match its manifest.");
                        }

                        break;

                    case DatabaseEntry when !hasDatabase:
                        hasDatabase = true;
                        await CopyEntryAsync(entry, targets.DatabaseDumpPath, overwrite: true, hash: false, ct);
                        break;

                    case var name when name.StartsWith(DocumentsPrefix, StringComparison.Ordinal) && IsSafeRelativePath(name[DocumentsPrefix.Length..]):
                        documentCount++;
                        documentBytes += entry.Length;
                        var relative = name[DocumentsPrefix.Length..];
                        var target = targets.DocumentsFolder is { } folder ? Path.Combine(folder, relative) : null;
                        var sha256 = await CopyEntryAsync(entry, target, overwrite: false, hash: true, ct);
                        if (!documentHashes.TryAdd(relative, sha256!))
                        {
                            throw new InvalidDataException($"The document {relative} is in the backup twice.");
                        }

                        break;

                    case DocumentHashesEntry when recordedHashes is null:
                        recordedHashes = await ReadDocumentHashesAsync(entry, ct);
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

        if (manifest.ErasureLedger is not null && erasureLedger is null)
        {
            throw new InvalidDataException("The backup is incomplete: the erasure ledger its manifest lists is missing.");
        }

        if (documentCount != manifest.DocumentCount || documentBytes != manifest.DocumentBytes)
        {
            throw new InvalidDataException($"The backup holds {documentCount} documents ({documentBytes} bytes); its manifest lists {manifest.DocumentCount} ({manifest.DocumentBytes} bytes).");
        }

        CheckDocumentHashes(manifest, documentHashes, recordedHashes);
        return new BackupContents(manifest, masterKey, documentCount, documentBytes)
        {
            RingKeys = listed.Select(id => ringKeys[id]).ToList(),
            AuditSealKey = auditSealKey,
            DocumentHashes = documentHashes,
            ErasureLedger = erasureLedger,
        };
    }

    /// <summary>
    /// Every document read must be the one whose hash the archive records, and every hash it records must be of a document
    /// it holds. An older backup records none: its documents are then known by the hash of what was read.
    /// </summary>
    private static void CheckDocumentHashes(BackupManifest manifest, Dictionary<string, string> read, Dictionary<string, string>? recorded)
    {
        if (recorded is null)
        {
            if (manifest.DocumentHashes)
            {
                throw new InvalidDataException("The backup is incomplete: the hashes of the documents its manifest lists are missing.");
            }

            return;
        }

        foreach (var (path, hash) in read)
        {
            if (!recorded.TryGetValue(path, out var expected) || !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The document {path} in the backup does not match the hash the backup records for it.");
            }
        }

        if (recorded.Keys.FirstOrDefault(path => !read.ContainsKey(path)) is { } absent)
        {
            throw new InvalidDataException($"The backup records the hash of the document {absent}, which it does not hold.");
        }
    }

    /// <summary>Reads <see cref="DocumentHashesEntry"/>: a path may be listed once, with a 64-digit hash, and must be a document's.</summary>
    private static async Task<Dictionary<string, string>> ReadDocumentHashesAsync(TarEntry entry, CancellationToken ct)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry.DataStream is null)
        {
            return hashes;
        }

        // Bytes that are not UTF-8 read as replacement characters, which no hash or path has: the line is then refused below.
        using var reader = new StreamReader(entry.DataStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            // "<64 hex digits>  files/<path>", as sha256sum writes a file read as text.
            var name = line.Length > 66 && line[64] == ' ' && line[65] == ' ' ? line[66..] : null;
            if (name is null || line.AsSpan(0, 64).ContainsAnyExcept(HexDigits) || !name.StartsWith(DocumentsPrefix, StringComparison.Ordinal)
                || !IsSafeRelativePath(name[DocumentsPrefix.Length..]) || !hashes.TryAdd(name[DocumentsPrefix.Length..], line[..64].ToLowerInvariant()))
            {
                throw new InvalidDataException("The hashes the backup records for its documents are not valid.");
            }
        }

        return hashes;
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

    /// <summary>
    /// Copies an entry's data to <paramref name="target"/> (or reads it through, when there is none or it is there already
    /// and not to be overwritten), and returns its SHA-256 (lower-case hex) when <paramref name="hash"/> asks for it.
    /// </summary>
    private static async Task<string?> CopyEntryAsync(TarEntry entry, string? target, bool overwrite, bool hash, CancellationToken ct)
    {
        using var sha256 = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        if (entry.DataStream is not null)
        {
            await using var output = target is null || (!overwrite && File.Exists(target)) ? null : CreateForWriting(target, overwrite);
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                int read;
                while ((read = await entry.DataStream.ReadAsync(buffer.AsMemory(0, 1 << 16), ct)) > 0)
                {
                    sha256?.AppendData(buffer, 0, read);
                    if (output is not null)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return sha256 is null ? null : Convert.ToHexStringLower(sha256.GetHashAndReset());
    }

    private static FileStream CreateForWriting(string target, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        return new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
    }

    /// <summary>
    /// A document's file as the tar writer reads it, hashed (SHA-256) on the way through. It tells the file's length and
    /// position, as the file does, because the writer puts the size in the entry's header before it copies the data.
    /// </summary>
    private sealed class HashingReadStream(FileStream file) : Stream
    {
        private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        /// <summary>The SHA-256 (lower-case hex) of everything read, once the writer has disposed of the stream.</summary>
        public string? Sha256 { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => file.CanSeek;

        public override bool CanWrite => false;

        public override long Length => file.Length;

        public override long Position
        {
            get => file.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = file.Read(buffer);
            _sha256.AppendData(buffer[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await file.ReadAsync(buffer, cancellationToken);
            _sha256.AppendData(buffer.Span[..read]);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Sha256 is null)
            {
                Sha256 = Convert.ToHexStringLower(_sha256.GetHashAndReset());
                _sha256.Dispose();
                file.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Sha256 is null)
            {
                Sha256 = Convert.ToHexStringLower(_sha256.GetHashAndReset());
                _sha256.Dispose();
                await file.DisposeAsync();
            }

            await base.DisposeAsync();
        }
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

    /// <summary>
    /// Documents that were here but whose bytes were not those the backup records (damaged on disk, or encrypted again
    /// since the backup was made), replaced by the backup's.
    /// </summary>
    public int DocumentsReplaced { get; init; }

    /// <summary>Where the files the backup's documents replaced are kept, when any were; null otherwise.</summary>
    public string? ReplacedDocumentsKeptIn { get; init; }

    /// <summary>Whether the backup carries the erasure ledger (older backups do not).</summary>
    public bool ErasureLedgerCarried { get; init; }

    /// <summary>The copy of the erasure ledger found beside the backup file, whose lines were taken too; null when there was none.</summary>
    public string? ErasureLedgerCopy { get; init; }

    /// <summary>How many lines of those ledgers this server's ledger did not have, added to it.</summary>
    public int ErasureLedgerLinesAdded { get; init; }
}

/// <summary>
/// Restores a backup on a Server Node, with the HC360 service stopped (the Server CLI's <c>backup restore</c>, run by
/// Restore-HC360.ps1). The whole archive is read and checked into a staging folder first; only then is the field master
/// key installed (wrapped for this machine) and are the documents moved into place, so a damaged backup changes nothing.
/// The database dump is written for pg_restore, which the script runs next.
/// </summary>
/// <summary>
/// Puts back what a backup holds besides its database, on the Server Node with the service stopped: the stored documents,
/// the erasures of its erasure ledger (added to this server's, which the next start applies), then the field encryption
/// key. The whole archive is read and checked first, and the key comes last, so a failure never
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

                // Documents first, each as the backup has it: one the database does not refer to is harmless, and one that
                // differs here is set aside, and put back should a later step fail. The key last: until it is installed,
                // this machine keeps the key its current database was encrypted with.
                var keptFolder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(directories.Files))!,
                    string.Create(CultureInfo.InvariantCulture, $"files-replaced-{time.GetUtcNow():yyyyMMdd-HHmmss}"));
                var documents = MoveDocuments(staging, directories.Files, contents.DocumentHashes, keptFolder);
                string? keptAs;
                ErasureLedgerRestore ledger;
                try
                {
                    // The erasures before the keys: the ledger is only ever added to, and the restore must not end with the
                    // backup's key in place but without the erasures that keep erased data from coming back with it.
                    ledger = RestoreErasureLedger(backupFile, contents.ErasureLedger, directories);

                    // The keys rotations added only ever join the ring (one already here stays), then the original key, then
                    // which key is current: data encrypted with any of them is readable once the database is back.
                    var protector = FieldMasterKey.CreateProtector(dataProtection);
                    foreach (var key in contents.RingKeys)
                    {
                        FieldKeyRing.Add(directories, protector, key);
                    }

                    keptAs = FieldMasterKey.Install(FieldMasterKey.PathIn(directories), protector, contents.MasterKey, time.GetUtcNow());

                    // The seals of the restored audit trail were made with the backup's audit key; a different one here is
                    // kept under a new name, as the field key is.
                    if (contents.AuditSealKey is { } auditSealKey)
                    {
                        FieldMasterKey.Install(Auditing.AuditSealKey.PathIn(directories), Auditing.AuditSealKey.CreateProtector(dataProtection), auditSealKey,
                            time.GetUtcNow());
                    }

                    // The backup decides which key encrypts new data, also when it is the original key (no current key in
                    // its manifest): a key this machine had made current before is not used again, in case it was exposed.
                    var current = contents.Manifest.CurrentKey;
                    FieldKeyRing.SetCurrent(directories, current is null || current == contents.Manifest.KeyFingerprint ? null : current);
                }
                catch
                {
                    // Restore-HC360.ps1 puts the previous database back: the documents it refers to go back with it.
                    documents.Undo();
                    throw;
                }

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

                return new BackupRestoreResult(payload.Version, contents.Manifest, documents.Restored, documents.Present, KeyInstalled: true, keptAs)
                {
                    RingKeysInstalled = contents.RingKeys.Count,
                    AuditSealKeyInstalled = contents.AuditSealKey is not null,
                    RecoveryKeyKept = recoveryKeyKept,
                    RecoveryKeyProblem = recoveryKeyProblem,
                    DocumentsReplaced = documents.Replaced.Count,
                    ReplacedDocumentsKeptIn = documents.Replaced.Count > 0 ? keptFolder : null,
                    ErasureLedgerCarried = contents.ErasureLedger is not null,
                    ErasureLedgerCopy = ledger.Copy,
                    ErasureLedgerLinesAdded = ledger.Added,
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

    /// <summary>
    /// Puts the backup's documents in place from <paramref name="staging"/>. One missing here is moved in. One here is
    /// compared with the hash the backup records for it: left when its bytes are the backup's, replaced when they are not
    /// (damaged on disk, or encrypted again with another key since the backup was made, which the restored database does
    /// not know). The file it replaces is moved to <paramref name="keptFolder"/>, never deleted.
    /// </summary>
    private static DocumentsPutBack MoveDocuments(string staging, string documentsFolder, IReadOnlyDictionary<string, string> hashes, string keptFolder)
    {
        var documents = new DocumentsPutBack(keptFolder);
        if (!Directory.Exists(staging))
        {
            return documents;
        }

        foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, source);
            var target = Path.Combine(documentsFolder, relative);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                documents.Restored++;
                continue;
            }

            if (hashes.TryGetValue(relative.Replace('\\', '/'), out var recorded) && string.Equals(Sha256Of(target), recorded, StringComparison.OrdinalIgnoreCase))
            {
                documents.Present++;
                continue;
            }

            var kept = Path.Combine(keptFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Move(target, kept);
            try
            {
                File.Move(source, target);
            }
            catch
            {
                File.Move(kept, target);
                throw;
            }

            documents.Replaced.Add((target, kept));
        }

        return documents;
    }

    /// <summary>
    /// Adds to this server's erasure ledger the lines of the ledger the backup carries and of the ledger's copy beside the
    /// backup file (the one kept with the backups' second copy, which outlives the server's disk), each line it lacks. The
    /// newest ledger available is then this server's, whichever it was: as HC360 starts, it applies again every erasure in
    /// it that the restored database does not have (DPDP Act 2023 s8(7)).
    /// </summary>
    private static ErasureLedgerRestore RestoreErasureLedger(string backupFile, byte[]? carried, DataDirectories directories)
    {
        var path = ErasureLedgerFiles.PathIn(directories);
        var lines = carried is null ? new List<string>() : new List<string>(ErasureLedgerFiles.Lines(carried));
        var copy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupFile))!, ErasureLedgerFiles.FileName);
        IReadOnlyList<string> copyLines = string.Equals(copy, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ? [] : ErasureLedgerFiles.ReadLines(copy);
        lines.AddRange(copyLines);
        return new ErasureLedgerRestore(ErasureLedgerFiles.Merge(path, lines), copyLines.Count > 0 ? copy : null);
    }

    private sealed record ErasureLedgerRestore(int Added, string? Copy);

    private static string Sha256Of(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    /// <summary>What <see cref="MoveDocuments"/> did, and how to undo the replacements it made.</summary>
    private sealed class DocumentsPutBack(string keptFolder)
    {
        public int Restored { get; set; }

        public int Present { get; set; }

        /// <summary>Each document replaced, and where the file that was there is kept.</summary>
        public List<(string Target, string Kept)> Replaced { get; } = [];

        /// <summary>Puts back, as far as it can, the files the backup's replaced: the restore failed after the documents.</summary>
        public void Undo()
        {
            foreach (var (target, kept) in Replaced)
            {
                try
                {
                    File.Move(kept, target, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // It stays in the kept folder, which then stays too.
                }
            }

            try
            {
                if (Directory.Exists(keptFolder) && !Directory.EnumerateFiles(keptFolder, "*", SearchOption.AllDirectories).Any())
                {
                    Directory.Delete(keptFolder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An empty folder left behind does no harm.
            }
        }
    }
}
