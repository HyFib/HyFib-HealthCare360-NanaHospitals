using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The SHA-256 of each stored document as the last backup found it, so a nightly backup reads only the documents that
/// are new or changed rather than every one (about 274 GB by year 3 under the design's assumptions). An entry is used
/// only while the file's length, last write time and creation time are all as they were: the file store writes each
/// document once, and moving one to a new key (<see cref="Security.FieldKeyMover"/>) writes a new file and renames it into
/// place, which changes them. A missing, damaged or foreign cache only costs reading everything once more.
/// <code>
/// zlib( "HC360HSH" | version 1 | count (int32 LE) | count × [ path length (uint16 LE) | path (UTF-8) | length (int64) | last write (int64 ticks UTC) | creation (int64 ticks UTC) | SHA-256 (32) ] )
/// </code>
/// It holds storage paths and hashes of encrypted files only, no content and no key, and lives in the primary backup
/// folder (<see cref="FileName"/>).
/// </summary>
internal sealed class BackupHashCache
{
    public const string FileName = "hc360-hashes.bin";
    private const byte Version = 1;
    private static readonly byte[] Magic = "HC360HSH"u8.ToArray();

    private readonly Dictionary<string, Entry> _entries;

    private BackupHashCache(Dictionary<string, Entry> entries) => _entries = entries;

    public int Count => _entries.Count;

    public static BackupHashCache Empty() => new(new Dictionary<string, Entry>(StringComparer.Ordinal));

    /// <summary>The cache saved in <paramref name="path"/>, or an empty one when there is none or it cannot be read.</summary>
    public static BackupHashCache Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty();
        }

        try
        {
            using var file = File.OpenRead(path);
            using var zlib = new ZLibStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(zlib, Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadByte() != Version)
            {
                return Empty();
            }

            var count = reader.ReadInt32();
            var entries = new Dictionary<string, Entry>(Math.Clamp(count, 0, 10_000_000), StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var pathBytes = reader.ReadBytes(reader.ReadUInt16());
                var entry = new Entry(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), Convert.ToHexStringLower(reader.ReadBytes(32)));
                entries[Encoding.UTF8.GetString(pathBytes)] = entry;
            }

            return new BackupHashCache(entries);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return Empty();
        }
    }

    /// <summary>Writes the cache beside <paramref name="path"/>, then replaces it, so a cut-off write leaves the old one.</summary>
    public void Save(string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zlib = new ZLibStream(file, CompressionLevel.Fastest, leaveOpen: true))
                using (var writer = new BinaryWriter(zlib, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(_entries.Count);
                    Span<byte> number = stackalloc byte[2];
                    foreach (var (relativePath, entry) in _entries)
                    {
                        var bytes = Encoding.UTF8.GetBytes(relativePath);
                        BinaryPrimitives.WriteUInt16LittleEndian(number, (ushort)bytes.Length);
                        writer.Write(number);
                        writer.Write(bytes);
                        writer.Write(entry.Length);
                        writer.Write(entry.LastWriteTicks);
                        writer.Write(entry.CreationTicks);
                        writer.Write(Convert.FromHexString(entry.Sha256));
                    }
                }

                file.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The document's SHA-256 as last recorded, when the file is still exactly as it was then; otherwise null.</summary>
    public string? Lookup(string relativePath, FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _entries.TryGetValue(relativePath, out var entry) && entry.Matches(file) ? entry.Sha256 : null;
    }

    public void Set(string relativePath, FileInfo file, string sha256)
    {
        ArgumentNullException.ThrowIfNull(file);
        _entries[relativePath] = new Entry(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks, sha256);
    }

    /// <summary>Forgets documents no longer in the store, so the cache does not grow with what was removed.</summary>
    public void KeepOnly(IReadOnlySet<string> relativePaths)
    {
        foreach (var gone in _entries.Keys.Where(p => !relativePaths.Contains(p)).ToList())
        {
            _entries.Remove(gone);
        }
    }

    /// <summary>
    /// The SHA-256 of a stored document: from the cache while the file is unchanged, otherwise read from the file and
    /// recorded.
    /// </summary>
    public async Task<string> HashAsync(string documentsFolder, BackupDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        var file = new FileInfo(Path.Combine(documentsFolder, document.RelativePath));
        if (Lookup(document.RelativePath, file) is { } known)
        {
            return known;
        }

        var sha256 = await BackupStore.HashFileAsync(file.FullName, ct);
        file.Refresh();
        Set(document.RelativePath, file, sha256);
        return sha256;
    }

    private sealed record Entry(long Length, long LastWriteTicks, long CreationTicks, string Sha256)
    {
        public bool Matches(FileInfo file) =>
            file.Exists && file.Length == Length && file.LastWriteTimeUtc.Ticks == LastWriteTicks && file.CreationTimeUtc.Ticks == CreationTicks;
    }
}
