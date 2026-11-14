using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>A stored document as a format 3 archive lists it: where it goes, and which copy in the backup folder it is.</summary>
/// <param name="RelativePath">The file store's path, relative to the documents folder (forward slashes).</param>
/// <param name="BlobId">The SHA-256 of the stored file (<see cref="BackupStore.HashFileAsync"/>), which names its copy.</param>
/// <param name="Length">The stored file's length in bytes.</param>
public sealed record BackupIndexedDocument(string RelativePath, string BlobId, long Length);

/// <summary>
/// The <c>documents.idx</c> entry of a format 3 archive: every stored document the backup needs, kept in the backup folder
/// (<see cref="BackupStore"/>) rather than in the archive. Compact and compressed: about 60 bytes a document, so a few
/// tens of megabytes for a million documents.
/// <code>
/// zlib( "HC360IDX" | version 1 (byte) | count (int32 LE) | count × [ path length (uint16 LE) | path (UTF-8) | SHA-256 (32 bytes) | length (int64 LE) ] )
/// </code>
/// The archive's manifest holds the SHA-256 of the compressed entry, and the archive is encrypted and authenticated as a
/// whole, so the index cannot be changed without the backup failing to open.
/// </summary>
public static class BackupDocumentIndex
{
    private const byte Version = 1;
    private static readonly byte[] Magic = "HC360IDX"u8.ToArray();

    /// <summary>Writes the index (compressed) and returns the SHA-256 of what was written.</summary>
    /// <exception cref="ArgumentException">A path is not the file store's, a name is not a SHA-256, or a path is listed twice.</exception>
    public static string Write(Stream output, IReadOnlyList<BackupIndexedDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (!BackupArchive.IsSafeRelativePath(document.RelativePath) || !paths.Add(document.RelativePath))
            {
                throw new ArgumentException($"'{document.RelativePath}' is not a stored document's path, or is listed twice.", nameof(documents));
            }

            if (!BackupStore.IsBlobId(document.BlobId) || document.Length < 0)
            {
                throw new ArgumentException($"The document {document.RelativePath} is not listed with its SHA-256 and length.", nameof(documents));
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var hashing = new HashingStream(output, hash))
        using (var zlib = new ZLibStream(hashing, CompressionLevel.Optimal, leaveOpen: true))
        {
            Span<byte> number = stackalloc byte[8];
            zlib.Write(Magic);
            zlib.WriteByte(Version);
            BinaryPrimitives.WriteInt32LittleEndian(number, documents.Count);
            zlib.Write(number[..4]);
            foreach (var document in documents)
            {
                var path = Encoding.UTF8.GetBytes(document.RelativePath);
                BinaryPrimitives.WriteUInt16LittleEndian(number, (ushort)path.Length);
                zlib.Write(number[..2]);
                zlib.Write(path);
                zlib.Write(Convert.FromHexString(document.BlobId));
                BinaryPrimitives.WriteInt64LittleEndian(number, document.Length);
                zlib.Write(number);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Reads an index written by <see cref="Write"/>, checking every entry, and the SHA-256 of the compressed bytes.</summary>
    /// <exception cref="InvalidDataException">The index is damaged, or names something that is not a stored document.</exception>
    public static async Task<(IReadOnlyList<BackupIndexedDocument> Documents, string Sha256)> ReadAsync(Stream input, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var documents = new List<BackupIndexedDocument>();
        await using (var hashing = new HashingStream(input, hash))
        {
            try
            {
                await using (var zlib = new ZLibStream(hashing, CompressionMode.Decompress, leaveOpen: true))
                {
                    var header = new byte[Magic.Length + 1 + 4];
                    await zlib.ReadExactlyAsync(header, ct);
                    if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || header[Magic.Length] != Version)
                    {
                        throw new InvalidDataException("The backup's document index is not in a format this version of HC360 reads.");
                    }

                    var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length + 1));
                    if (count < 0)
                    {
                        throw new InvalidDataException("The backup's document index is damaged.");
                    }

                    var paths = new HashSet<string>(StringComparer.Ordinal);
                    var fixedPart = new byte[32 + 8];
                    var lengthBytes = new byte[2];
                    for (var i = 0; i < count; i++)
                    {
                        await zlib.ReadExactlyAsync(lengthBytes, ct);
                        var path = new byte[BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes)];
                        await zlib.ReadExactlyAsync(path, ct);
                        await zlib.ReadExactlyAsync(fixedPart, ct);
                        var relativePath = Encoding.UTF8.GetString(path);
                        var length = BinaryPrimitives.ReadInt64LittleEndian(fixedPart.AsSpan(32));
                        if (!BackupArchive.IsSafeRelativePath(relativePath) || !paths.Add(relativePath) || length < 0)
                        {
                            throw new InvalidDataException($"The backup's document index lists '{relativePath}', which is not a stored document's path, or lists it twice.");
                        }

                        documents.Add(new BackupIndexedDocument(relativePath, Convert.ToHexStringLower(fixedPart.AsSpan(0, 32)), length));
                    }

                    if (await zlib.ReadAsync(new byte[1], ct) != 0)
                    {
                        throw new InvalidDataException("The backup's document index has more in it than it lists.");
                    }
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("The backup's document index is incomplete.");
            }

            // What zlib did not need is part of the entry too, and of its hash.
            await hashing.CopyToAsync(Stream.Null, ct);
        }

        return (documents, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Passes bytes through, adding them to a hash; never disposes the stream under it.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            hash.AppendData(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            inner.Write(buffer);
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
