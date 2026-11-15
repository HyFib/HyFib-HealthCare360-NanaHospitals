using System.Buffers;
using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>What an archive backup holds, written first so a restore can check everything that follows.</summary>
/// <param name="Format">The archive format (<see cref="BackupArchive.Format"/>).</param>
/// <param name="Kind">Scheduled, Manual or PreMigration.</param>
/// <param name="Migrations">The EF migrations applied to the database when it was dumped (its schema version).</param>
/// <param name="IncludesDocuments">False for the database-only backup taken before an update.</param>
/// <param name="KeyFingerprint">The fingerprint of the escrowed field master key (<see cref="FieldMasterKey.Fingerprint"/>).</param>
/// <param name="RingKeys">The ids of the keys rotations added to the ring (<see cref="FieldKeyRing"/>), escrowed too; none before the first rotation.</param>
/// <param name="CurrentKey">Which key of the ring was current, or null for the original key.</param>
/// <param name="AuditSealKey">The fingerprint of the escrowed audit seal key (<see cref="Auditing.AuditSealKey"/>); null in older backups.</param>
/// <param name="DocumentIndexSha256">
/// Format 3: the SHA-256 of the <c>documents.idx</c> entry (<see cref="BackupDocumentIndex"/>), which records the SHA-256 of
/// every document the backup needs; null otherwise.
/// </param>
/// <param name="RowCounts">
/// The rows of the key tables in the dump, counted in the very snapshot pg_dump dumped (<see cref="BackupRowCounts"/>), which
/// a test restore checks the restored database against; null in older backups.
/// </param>
/// <param name="DocumentHashes">
/// Format 2 only: whether the archive records the SHA-256 of each document it holds, as it was copied
/// (<see cref="BackupArchive.DocumentHashesEntry"/>, after the documents), as some format 2 backups do; false otherwise.
/// A format 3 archive's index records each document's SHA-256 itself.
/// </param>
/// <param name="ErasureLedger">
/// The SHA-256 of the erasure ledger the archive carries (<see cref="BackupArchive.ErasureLedgerEntry"/>); null in older
/// backups and in the backup before an update. The writer sets it from what it writes.
/// </param>
/// <param name="HospitalCa">
/// The fingerprint, as HC360 Desktop pins it, of the hospital CA the archive carries (<see cref="BackupArchive.HospitalCaEntry"/>);
/// null in older backups, in the backup before an update and when the server had no CA of its own.
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
    string? DocumentIndexSha256 = null,
    IReadOnlyDictionary<string, long>? RowCounts = null,
    bool DocumentHashes = false,
    string? ErasureLedger = null,
    string? HospitalCa = null);

/// <summary>A stored document file, relative to the documents folder (forward slashes).</summary>
public sealed record BackupDocument(string RelativePath, long Length);

/// <summary>
/// What a backup carries besides the database, the keys and the documents: what is kept outside the database and a restore
/// on new hardware needs. Each is left out when null.
/// </summary>
/// <param name="ErasureLedger">The erasure ledger's lines, as <see cref="ErasureLedgerFiles.ReadForBackup"/> reads them.</param>
/// <param name="HospitalCa">The hospital CA with its private key, as <see cref="LanCertificateManager.ExportForBackup"/> exports it.</param>
public sealed record BackupExtras(byte[]? ErasureLedger = null, ExportedHospitalCa? HospitalCa = null);

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
    /// Format 3: the documents the backup needs, each with its SHA-256 and length, which are kept in the backup folder beside
    /// it (<see cref="BackupStore"/>) rather than in the archive. Empty for format 2, whose documents are inside.
    /// </summary>
    public IReadOnlyList<BackupIndexedDocument> Index { get; init; } = [];

    /// <summary>
    /// Format 2: the SHA-256 (lower-case hex) of each document the archive holds, by its path relative to the documents
    /// folder: the hash the archive records, which its copy matched, or, in a backup that records none, the hash of its
    /// copy. Empty for format 3, whose <see cref="Index"/> gives each document's SHA-256.
    /// </summary>
    public IReadOnlyDictionary<string, string> DocumentHashes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The erasure ledger the backup carries, as the server had it when the backup was made; null in older backups.</summary>
    public byte[]? ErasureLedger { get; init; }

    /// <summary>
    /// The hospital CA with its private key (PKCS#12, no password of its own), when the backup carries it: its caller zeroes
    /// it once done, as it does the keys.
    /// </summary>
    public byte[]? HospitalCa { get; init; }
}

/// <summary>
/// The payload of an archive backup (DED-16): one tar archive with the manifest, an escrow copy of the field master key,
/// the hospital CA and the erasure ledger, and the PostgreSQL dump. The archive itself is encrypted by
/// <see cref="BackupEncryption"/> with the recovery key administrators keep offline (<see cref="BackupRecoveryKey"/>). For
/// full backups, the stored documents (each already encrypted with the field keys):
/// <list type="bullet">
/// <item>Format 3 (<see cref="Format"/>): an index of them, with the SHA-256 of each (<see cref="BackupDocumentIndex"/>).
/// Each document is kept once in the backup folder beside the archive (<see cref="BackupStore"/>), so the folder and the
/// recovery key restore everything on replacement hardware.</item>
/// <item>Format 2 (<see cref="InlineDocumentsFormat"/>): the documents themselves, exactly as they are on disk, and in some
/// backups the SHA-256 of each (<see cref="DocumentHashesEntry"/>). Still read; one such file and its recovery key restore
/// everything. The backup before an update is still written so, without documents, for the version updated from.</item>
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

    /// <summary>
    /// Format 2 only: the SHA-256 of every document, after the documents, as each was copied: one line each, as sha256sum
    /// writes them (<c>&lt;hash&gt;  files/2026/09/&lt;id&gt;.bin</c>). Read, never written now: format 3 records each
    /// document's SHA-256 in its index.
    /// </summary>
    public const string DocumentHashesEntry = "documents.sha256";

    /// <summary>
    /// The erasure ledger (DPDP Act 2023 s8(7)), as the server had it once the database was dumped: inside the encryption,
    /// so every copy of the backup, the off-site one included, carries it to wherever the backup is restored.
    /// </summary>
    public const string ErasureLedgerEntry = "privacy/" + ErasureLedgerFiles.FileName;

    /// <summary>
    /// The hospital CA with its private key (<see cref="LanCertificateManager.ExportForBackup"/>), beside the field keys:
    /// protected, as they are and as the database is, by the archive's encryption alone.
    /// </summary>
    public const string HospitalCaEntry = "keys/hospital-ca.pfx";

    /// <summary>The largest hospital CA file a backup carries: a few kilobytes in practice.</summary>
    private const int MaxHospitalCaLength = 1024 * 1024;

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

    /// <summary>
    /// Writes a format 2 archive, with the documents inside, as every earlier version reads it: the manifest, the keys, the
    /// database dump and the documents, and nothing else. The backup before an update is written so, without documents,
    /// because undoing the update restores it with the version updated from (DEPLOYMENT.md section 7, "Going back"). Full
    /// backups are written with <see cref="WriteIndexedAsync(Stream, BackupManifest, byte[], IReadOnlyList{byte[]}, byte[], string, IReadOnlyList{BackupIndexedDocument}, BackupExtras, CancellationToken)"/>.
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
        var inline = manifest with { Format = InlineDocumentsFormat, DocumentIndexSha256 = null, DocumentHashes = false, ErasureLedger = null, HospitalCa = null };
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

    /// <summary>A format 3 archive without anything besides the database, the keys and the index (<see cref="BackupExtras"/>).</summary>
    public static Task<BackupManifest> WriteIndexedAsync(
        Stream output,
        BackupManifest manifest,
        byte[] masterKey,
        IReadOnlyList<byte[]> ringKeys,
        byte[]? auditSealKey,
        string databaseDumpPath,
        IReadOnlyList<BackupIndexedDocument>? documents,
        CancellationToken ct) =>
        WriteIndexedAsync(output, manifest, masterKey, ringKeys, auditSealKey, databaseDumpPath, documents, new BackupExtras(), ct);

    /// <summary>
    /// Writes a format 3 archive: the manifest, the keys, the hospital CA and the erasure ledger (<paramref name="extras"/>),
    /// the index of the documents (for a full backup), which records the SHA-256 and length of each, and the database dump.
    /// The documents themselves must be in the backup folder already. The manifest is completed here, so its format, document
    /// count, bytes, index hash and the hashes of what it carries always agree with what is written.
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
        BackupExtras extras,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(extras);
        var index = new MemoryStream();
        var indexSha256 = documents is null ? null : BackupDocumentIndex.Write(index, documents);

        // The manifest says what follows, so a restore can tell an archive cut short from an older one.
        var written = manifest with
        {
            Format = Format,
            IncludesDocuments = documents is not null,
            DocumentCount = documents?.Count ?? 0,
            DocumentBytes = documents?.Sum(d => d.Length) ?? 0,
            DocumentIndexSha256 = indexSha256,
            DocumentHashes = false,
            ErasureLedger = extras.ErasureLedger is { } ledger ? Convert.ToHexStringLower(SHA256.HashData(ledger)) : null,
            HospitalCa = extras.HospitalCa?.Fingerprint,
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

        if (extras.HospitalCa is { } hospitalCa)
        {
            await WriteEntryAsync(tar, HospitalCaEntry, new MemoryStream(hospitalCa.Pkcs12, writable: false), ct);
        }

        if (extras.ErasureLedger is { } erasures)
        {
            await WriteEntryAsync(tar, ErasureLedgerEntry, new MemoryStream(erasures, writable: false), ct);
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
    /// Reads a whole archive: the manifest first, then the key (which must match the manifest's fingerprint), what it
    /// carries besides (each matching the hash or fingerprint its manifest names), the database dump and the documents:
    /// format 3's index, whose hash must be the manifest's, or format 2's documents, whose hashes must match those the
    /// archive records when it records them; either way their count and size must match the manifest. Documents already
    /// present in <see cref="BackupReadTargets.DocumentsFolder"/> are left as they are; a restore reads into an empty folder
    /// and compares them itself (<see cref="BackupRestore"/>).
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
        var documentHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string>? recordedHashes = null;
        byte[]? erasureLedger = null;
        byte[]? hospitalCa = null;

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

                    case HospitalCaEntry when hospitalCa is null:
                        hospitalCa = await ReadSmallAsync(entry, MaxHospitalCaLength, ct);
                        if (manifest.HospitalCa is null || LanCertificateManager.BackupFingerprint(hospitalCa) != manifest.HospitalCa)
                        {
                            throw new InvalidDataException("The hospital CA in the backup does not match its manifest.");
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
                        var relative = name[DocumentsPrefix.Length..];
                        var target = targets.DocumentsFolder is { } folder ? Path.Combine(folder, relative) : null;
                        var sha256 = await CopyEntryAsync(entry, target, overwrite: false, hash: true, ct);
                        if (!documentHashes.TryAdd(relative, sha256!))
                        {
                            throw new InvalidDataException($"The document {relative} is in the backup twice.");
                        }

                        break;

                    case DocumentHashesEntry when manifest.Format == InlineDocumentsFormat && recordedHashes is null:
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

        if (manifest.HospitalCa is not null && hospitalCa is null)
        {
            throw new InvalidDataException("The backup is incomplete: the hospital CA its manifest lists is missing.");
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

        if (manifest.Format == InlineDocumentsFormat)
        {
            CheckDocumentHashes(manifest, documentHashes, recordedHashes);
        }

        return new BackupContents(manifest, masterKey, documentCount, documentBytes)
        {
            RingKeys = listed.Select(id => ringKeys[id]).ToList(),
            AuditSealKey = auditSealKey,
            Index = index ?? [],
            DocumentHashes = documentHashes,
            ErasureLedger = erasureLedger,
            HospitalCa = hospitalCa,
        };
    }

    /// <summary>
    /// Format 2: every document read must be the one whose hash the archive records, and every hash it records must be of a
    /// document it holds. A backup that records none is known by the hash of what was read.
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
}

/// <summary>The outcome of <see cref="BackupRestore.RestoreAsync(string, byte[], string, DataDirectories, IDataProtectionProvider, IDataProtectionProvider, TimeProvider, CancellationToken, string)"/>.</summary>
/// <param name="Manifest">Null for a version 1 backup, which holds the database only.</param>
/// <param name="DocumentsAlreadyPresent">Documents that were here already, their bytes those the backup records.</param>
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

    /// <summary>Whether the backup carries the erasure ledger (older backups, and the one before an update, do not).</summary>
    public bool ErasureLedgerCarried { get; init; }

    /// <summary>The copy of the erasure ledger found beside the backup file, whose lines were taken too; null when there was none.</summary>
    public string? ErasureLedgerCopy { get; init; }

    /// <summary>How many lines of those ledgers this server's ledger did not have, added to it.</summary>
    public int ErasureLedgerLinesAdded { get; init; }

    /// <summary>What became of the hospital CA the backup carries; null when it carries none (older backups) or that failed.</summary>
    public RestoredHospitalCa? HospitalCa { get; init; }

    /// <summary>Why the backup's hospital CA could not be installed, when that failed after everything else was restored.</summary>
    public string? HospitalCaProblem { get; init; }
}

/// <summary>
/// Puts back what a backup holds besides its database, on the Server Node with the service stopped (the Server CLI's
/// <c>backup restore</c>, run by Restore-HC360.ps1): the stored documents, the erasures of its erasure ledger (added to this
/// server's, which the next start applies), then the field encryption key, and last the hospital CA the devices trust. The
/// whole archive is read and checked into a staging folder first, a format 3 backup's documents with it from its backup
/// folder (each against the SHA-256 its index records), so a damaged backup changes nothing; and the key comes last of
/// what the database needs, so a failure never leaves this machine with the backup's key but not its database.
/// Restore-HC360.ps1 restores the database first, runs this after it succeeds, and puts the previous database back if this
/// fails.
/// </summary>
public static class BackupRestore
{
    /// <summary>Restores a backup on a single Server Node, whose hospital CA is protected with the same key ring as its other keys.</summary>
    /// <param name="databaseDumpPath">Where to write the database dump as well, or null to only check it.</param>
    /// <param name="blobsFolder">Where a format 3 backup's documents are, when not in the folder the archive is in.</param>
    public static Task<BackupRestoreResult> RestoreAsync(
        string backupFile,
        byte[] recoveryKey,
        string? databaseDumpPath,
        DataDirectories directories,
        IDataProtectionProvider dataProtection,
        TimeProvider time,
        CancellationToken ct,
        string? blobsFolder = null) =>
        RestoreAsync(backupFile, recoveryKey, databaseDumpPath, directories, dataProtection, dataProtection, time, ct, blobsFolder);

    /// <param name="databaseDumpPath">Where to write the database dump as well, or null to only check it.</param>
    /// <param name="dataProtection">The key ring that protects the keys (<see cref="HospitalDataProtection.CreateProvider"/>).</param>
    /// <param name="nodeProtection">
    /// The key ring of this machine alone, which protects its hospital CA (<see cref="HospitalDataProtection.CreateNodeProvider"/>):
    /// the same as <paramref name="dataProtection"/> on a single Server Node.
    /// </param>
    /// <param name="blobsFolder">Where a format 3 backup's documents are, when not in the folder the archive is in.</param>
    public static async Task<BackupRestoreResult> RestoreAsync(
        string backupFile,
        byte[] recoveryKey,
        string? databaseDumpPath,
        DataDirectories directories,
        IDataProtectionProvider dataProtection,
        IDataProtectionProvider nodeProtection,
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

                // Documents first, each as the backup has it: one the database does not refer to is harmless, and one that
                // differs here is set aside, and put back should a later step fail. The key last: until it is installed,
                // this machine keeps the key its current database was encrypted with.
                var keptFolder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(directories.Files))!,
                    string.Create(CultureInfo.InvariantCulture, $"files-replaced-{time.GetUtcNow():yyyyMMdd-HHmmss}"));
                var documents = MoveDocuments(staging, directories.Files, ExpectedHashes(contents), keptFolder);
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

                // The hospital CA, which every device pins, with the database it was backed up with: on new hardware the
                // server they find then serves a certificate they trust. Reported, as the recovery key is, if it fails.
                RestoredHospitalCa? hospitalCa = null;
                string? hospitalCaProblem = null;
                if (contents.HospitalCa is { } carriedCa)
                {
                    try
                    {
                        hospitalCa = LanCertificateManager.RestoreCa(directories, nodeProtection, carriedCa, time.GetUtcNow());
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException)
                    {
                        hospitalCaProblem = ex.Message;
                    }
                }

                return new BackupRestoreResult(payload.Version, contents.Manifest, documents.Restored, documents.Present, KeyInstalled: true, keptAs)
                {
                    HospitalCa = hospitalCa,
                    HospitalCaProblem = hospitalCaProblem,
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

                if (contents.HospitalCa is { } ca)
                {
                    CryptographicOperations.ZeroMemory(ca);
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
    /// The SHA-256 each document the backup restores must have, by its path relative to the documents folder: a format 3
    /// backup's index records it (it names the document's copy in the backup folder); a format 2 backup records it beside
    /// its documents, or is known by the hash of its own copy (<see cref="BackupContents.DocumentHashes"/>).
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExpectedHashes(BackupContents contents) =>
        contents.Manifest.Format == BackupArchive.Format
            ? contents.Index.ToDictionary(d => d.RelativePath, d => d.BlobId, StringComparer.Ordinal)
            : contents.DocumentHashes;

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
