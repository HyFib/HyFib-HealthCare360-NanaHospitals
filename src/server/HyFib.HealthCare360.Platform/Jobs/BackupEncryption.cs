using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// Streaming authenticated encryption for large backups. A file is a header (8-byte magic, then the chunk size) and
/// fixed-size chunks, each AES-256-GCM with a random nonce. Every chunk's associated data holds its index and whether it
/// is the last one, so reordered, truncated or extended files do not decrypt.
/// <list type="bullet">
/// <item>Version 1 (<c>HC360BK1</c>): the payload is a pg_dump archive. Still read, no longer written.</item>
/// <item>Version 2 (<c>HC360BK2</c>): the payload is a <see cref="BackupArchive"/>, and the header is also part of every
/// chunk's associated data. Still read, no longer written.</item>
/// <item>Version 3 (<c>HC360BK3</c>): as version 2, and the header also names the recovery key that opens the file
/// (<see cref="BackupRecoveryKey.Id"/>), so a restore given another key says which one it needs.</item>
/// </list>
/// </summary>
public static class BackupEncryption
{
    public const int LegacyVersion = 1;
    public const int ArchiveVersion = 2;

    /// <summary>An archive whose header names the recovery key that opens it.</summary>
    public const int KeyedVersion = 3;
    internal const int DefaultChunkSize = 1024 * 1024;
    internal const int HeaderSize = 12;
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    private const int MaxChunkSize = 64 * 1024 * 1024;
    private static readonly byte[] MagicV1 = "HC360BK1"u8.ToArray();
    private static readonly byte[] MagicV2 = "HC360BK2"u8.ToArray();
    private static readonly byte[] MagicV3 = "HC360BK3"u8.ToArray();

    /// <summary>
    /// A write-only stream that encrypts into a version 3 file, which names <paramref name="key"/> by its id. Call
    /// <see cref="BackupEncryptor.CompleteAsync"/> after the last write; an encryptor disposed without it leaves the file
    /// truncated, so an interrupted backup never decrypts.
    /// </summary>
    public static BackupEncryptor CreateEncryptor(Stream output, byte[] key, bool leaveOpen = true) =>
        new(output, key, KeyedVersion, Header(KeyedVersion, DefaultChunkSize, key), leaveOpen);

    /// <summary>A version 2 encryptor, whose file does not name its key. Only for tests that need a backup in that format.</summary>
    internal static BackupEncryptor CreateUnnamedEncryptor(Stream output, byte[] key) =>
        new(output, key, ArchiveVersion, Header(ArchiveVersion, DefaultChunkSize), leaveOpen: true);

    /// <summary>Reads the header and returns a read-only stream of the decrypted payload (any version).</summary>
    /// <exception cref="BackupKeyMismatchException">The file names another recovery key (version 3).</exception>
    /// <exception cref="CryptographicException">Not an HC360 backup, the wrong key, or a damaged file.</exception>
    public static async Task<BackupDecryptor> OpenAsync(Stream input, byte[] key, CancellationToken ct, bool leaveOpen = true)
    {
        var header = new byte[HeaderSize];
        if (await ReadFullAsync(input, header, ct) != HeaderSize)
        {
            throw new CryptographicException("Not an HC360 backup file.");
        }

        var version = header.AsSpan(0, 8).SequenceEqual(MagicV3) ? KeyedVersion
            : header.AsSpan(0, 8).SequenceEqual(MagicV2) ? ArchiveVersion
            : header.AsSpan(0, 8).SequenceEqual(MagicV1) ? LegacyVersion
            : throw new CryptographicException("Not an HC360 backup file.");
        var chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        if (chunkSize is <= 0 or > MaxChunkSize)
        {
            throw new CryptographicException("Corrupt backup header.");
        }

        string? keyId = null;
        if (version == KeyedVersion)
        {
            Array.Resize(ref header, HeaderSize + BackupRecoveryKey.IdLength);
            if (await ReadFullAsync(input, header.AsMemory(HeaderSize), ct) != BackupRecoveryKey.IdLength)
            {
                throw new CryptographicException("The backup file is truncated.");
            }

            // The id is authenticated with every chunk, so a changed one does not decrypt either.
            keyId = Convert.ToHexString(header, HeaderSize, BackupRecoveryKey.IdLength);
            var given = BackupRecoveryKey.Id(key);
            if (given != keyId)
            {
                throw new BackupKeyMismatchException(keyId, given);
            }
        }

        return new BackupDecryptor(input, key, version, header, chunkSize, leaveOpen) { KeyId = keyId };
    }

    /// <summary>Decrypts a whole backup file into <paramref name="output"/> and returns its format version.</summary>
    public static async Task<int> DecryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct)
    {
        await using var payload = await OpenAsync(input, key, ct);
        await payload.CopyToAsync(output, ct);
        return payload.Version;
    }

    /// <summary>Writes a version 1 file. Only for tests that need a backup in the old format.</summary>
    internal static async Task EncryptLegacyAsync(Stream input, Stream output, byte[] key, CancellationToken ct, int chunkSize = DefaultChunkSize)
    {
        await using var encryptor = new BackupEncryptor(output, key, LegacyVersion, Header(LegacyVersion, chunkSize), leaveOpen: true);
        await input.CopyToAsync(encryptor, ct);
        await encryptor.CompleteAsync(ct);
    }

    /// <param name="key">The recovery key a version 3 file names.</param>
    internal static byte[] Header(int version, int chunkSize, byte[]? key = null)
    {
        var header = new byte[version == KeyedVersion ? HeaderSize + BackupRecoveryKey.IdLength : HeaderSize];
        (version switch { KeyedVersion => MagicV3, ArchiveVersion => MagicV2, _ => MagicV1 }).CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), chunkSize);
        if (version == KeyedVersion)
        {
            Convert.FromHexString(BackupRecoveryKey.Id(key ?? throw new ArgumentNullException(nameof(key)))).CopyTo(header, HeaderSize);
        }

        return header;
    }

    internal static byte[] AssociatedData(int version, byte[] header, long index, bool isFinal)
    {
        var prefix = version >= ArchiveVersion ? header.Length : 0;
        var data = new byte[prefix + 9];
        if (prefix > 0)
        {
            header.CopyTo(data, 0);
        }

        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(prefix), index);
        data[prefix + 8] = isFinal ? (byte)1 : (byte)0;
        return data;
    }

    internal static async ValueTask<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    internal static int ReadFull(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

/// <summary>Encrypts everything written to it into the backup format and hashes the file it produces.</summary>
public sealed class BackupEncryptor : Stream
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly AesGcm _aes;
    private readonly int _version;
    private readonly byte[] _header;
    private readonly byte[] _buffer;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _count;
    private long _index;
    private bool _headerWritten;
    private bool _completed;
    private bool _disposed;

    internal BackupEncryptor(Stream output, byte[] key, int version, byte[] header, bool leaveOpen)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        _aes = new AesGcm(key, BackupEncryption.TagSize);
        _version = version;
        _header = header;
        _buffer = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8))];
    }

    /// <summary>SHA-256 (hex) of the encrypted file, available after <see cref="CompleteAsync"/>.</summary>
    public string? Sha256 { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_completed && !_disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Writes the last chunk. Without it the file stays deliberately incomplete.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        EnsureWritable();
        await _output.WriteAsync(Seal(isFinal: true), ct);
        await _output.FlushAsync(ct);
        _completed = true;
        Sha256 = Convert.ToHexString(_hash.GetHashAndReset());
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        while (!buffer.IsEmpty)
        {
            // A full chunk is sealed only once more data arrives, so the last chunk is always the one marked final.
            if (_count == _buffer.Length)
            {
                _output.Write(Seal(isFinal: false));
            }

            var take = Math.Min(buffer.Length, _buffer.Length - _count);
            buffer[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            buffer = buffer[take..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        while (!buffer.IsEmpty)
        {
            if (_count == _buffer.Length)
            {
                await _output.WriteAsync(Seal(isFinal: false), cancellationToken);
            }

            var take = Math.Min(buffer.Length, _buffer.Length - _count);
            buffer.Span[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            buffer = buffer[take..];
        }
    }

    // Chunks are sealed only when full or complete; flushing never cuts a chunk short.
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _aes.Dispose();
            _hash.Dispose();
            CryptographicOperations.ZeroMemory(_buffer);
            if (!_leaveOpen)
            {
                _output.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _aes.Dispose();
            _hash.Dispose();
            CryptographicOperations.ZeroMemory(_buffer);
            if (!_leaveOpen)
            {
                await _output.DisposeAsync();
            }
        }

        await base.DisposeAsync();
    }

    private byte[] Seal(bool isFinal)
    {
        var headerLength = _headerWritten ? 0 : _header.Length;
        var frame = new byte[headerLength + BackupEncryption.NonceSize + 5 + BackupEncryption.TagSize + _count];
        if (!_headerWritten)
        {
            _header.CopyTo(frame, 0);
            _headerWritten = true;
        }

        var nonce = frame.AsSpan(headerLength, BackupEncryption.NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var lengthOffset = headerLength + BackupEncryption.NonceSize;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(lengthOffset, 4), _count);
        frame[lengthOffset + 4] = isFinal ? (byte)1 : (byte)0;
        var tag = frame.AsSpan(lengthOffset + 5, BackupEncryption.TagSize);
        var cipher = frame.AsSpan(lengthOffset + 5 + BackupEncryption.TagSize, _count);
        _aes.Encrypt(nonce, _buffer.AsSpan(0, _count), cipher, tag, BackupEncryption.AssociatedData(_version, _header, _index, isFinal));

        _index++;
        _count = 0;
        _hash.AppendData(frame);
        return frame;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The backup file is already complete.");
        }
    }
}

/// <summary>
/// The decrypted payload of a backup file, read chunk by chunk. Each chunk is authenticated before any of its bytes are
/// returned, and reaching the end also checks that the last chunk was marked final and nothing follows it.
/// </summary>
public sealed class BackupDecryptor : Stream
{
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly int _chunkSize;
    private readonly byte[] _frame = new byte[BackupEncryption.NonceSize + 5 + BackupEncryption.TagSize];
    private byte[] _plain = [];
    private int _offset;
    private int _length;
    private long _index;
    private bool _finalRead;
    private bool _disposed;

    internal BackupDecryptor(Stream input, byte[] key, int version, byte[] header, int chunkSize, bool leaveOpen)
    {
        _input = input;
        _leaveOpen = leaveOpen;
        _aes = new AesGcm(key, BackupEncryption.TagSize);
        Version = version;
        _header = header;
        _chunkSize = chunkSize;
    }

    /// <summary><see cref="BackupEncryption.LegacyVersion"/>, <see cref="BackupEncryption.ArchiveVersion"/> or <see cref="BackupEncryption.KeyedVersion"/>.</summary>
    public int Version { get; }

    /// <summary>The id of the recovery key the file names (version 3); null for older files, made with the original key.</summary>
    public string? KeyId { get; init; }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_offset == _length)
        {
            if (_finalRead)
            {
                return 0;
            }

            var headerRead = BackupEncryption.ReadFull(_input, _frame);
            var cipher = ChunkCipher(headerRead);
            if (BackupEncryption.ReadFull(_input, cipher) != cipher.Length)
            {
                throw Truncated();
            }

            Open(cipher);
            if (_finalRead && _input.ReadByte() != -1)
            {
                throw new CryptographicException("The backup file has unexpected data after its end.");
            }
        }

        return Take(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_offset == _length)
        {
            if (_finalRead)
            {
                return 0;
            }

            var headerRead = await BackupEncryption.ReadFullAsync(_input, _frame, cancellationToken);
            var cipher = ChunkCipher(headerRead);
            if (await BackupEncryption.ReadFullAsync(_input, cipher, cancellationToken) != cipher.Length)
            {
                throw Truncated();
            }

            Open(cipher);
            if (_finalRead && await BackupEncryption.ReadFullAsync(_input, new byte[1], cancellationToken) != 0)
            {
                throw new CryptographicException("The backup file has unexpected data after its end.");
            }
        }

        return Take(buffer.Span);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
            if (!_leaveOpen)
            {
                _input.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
            if (!_leaveOpen)
            {
                await _input.DisposeAsync();
            }
        }

        await base.DisposeAsync();
    }

    private static CryptographicException Truncated() => new("The backup file is truncated.");

    private byte[] ChunkCipher(int frameRead)
    {
        if (frameRead != _frame.Length)
        {
            throw Truncated();
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(_frame.AsSpan(BackupEncryption.NonceSize, 4));
        var isFinal = _frame[BackupEncryption.NonceSize + 4] == 1;

        // Only the last chunk may be shorter than the chunk size.
        if (length < 0 || length > _chunkSize || (!isFinal && length != _chunkSize) || _frame[BackupEncryption.NonceSize + 4] > 1)
        {
            throw new CryptographicException("Corrupt backup chunk.");
        }

        return new byte[length];
    }

    private void Open(byte[] cipher)
    {
        var nonce = _frame.AsSpan(0, BackupEncryption.NonceSize);
        var isFinal = _frame[BackupEncryption.NonceSize + 4] == 1;
        var tag = _frame.AsSpan(BackupEncryption.NonceSize + 5, BackupEncryption.TagSize);
        var plain = new byte[cipher.Length];
        _aes.Decrypt(nonce, cipher, tag, plain, BackupEncryption.AssociatedData(Version, _header, _index, isFinal));

        CryptographicOperations.ZeroMemory(_plain);
        _plain = plain;
        _offset = 0;
        _length = plain.Length;
        _index++;
        _finalRead = isFinal;
    }

    private int Take(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _length - _offset);
        _plain.AsSpan(_offset, count).CopyTo(destination);
        _offset += count;
        return count;
    }
}
