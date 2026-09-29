using System.Buffers.Binary;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class BackupEncryptionTests
{
    private const int Chunk = 16;
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData((3 * Chunk) + 5)]
    [InlineData(4 * Chunk)]
    public async Task An_archive_backup_decrypts_to_exactly_what_was_written(int length)
    {
        var payload = RandomNumberGenerator.GetBytes(length);

        var file = await EncryptAsync(payload);
        var (version, plain) = await DecryptAsync(file);

        version.ShouldBe(BackupEncryption.ArchiveVersion);
        plain.ShouldBe(payload);
    }

    [Fact]
    public async Task Writes_split_across_many_calls_decrypt_the_same()
    {
        var payload = RandomNumberGenerator.GetBytes(100);
        var output = new MemoryStream();
        await using (var encryptor = NewEncryptor(output))
        {
            for (var offset = 0; offset < payload.Length; offset += 7)
            {
                encryptor.Write(payload.AsSpan(offset, Math.Min(7, payload.Length - offset)));
            }

            await encryptor.CompleteAsync(CancellationToken.None);
        }

        (await DecryptAsync(output.ToArray())).Plain.ShouldBe(payload);
    }

    [Fact]
    public async Task A_backup_written_by_the_previous_version_still_decrypts()
    {
        var payload = RandomNumberGenerator.GetBytes((2 * Chunk) + 3);
        var file = PreviousVersionWriter.Encrypt(payload, Key, Chunk);

        var (version, plain) = await DecryptAsync(file);

        version.ShouldBe(BackupEncryption.LegacyVersion);
        plain.ShouldBe(payload);
    }

    [Fact]
    public async Task The_hash_is_of_the_file_that_was_written()
    {
        var output = new MemoryStream();
        await using var encryptor = NewEncryptor(output);
        await encryptor.WriteAsync(RandomNumberGenerator.GetBytes(40), TestContext.Current.CancellationToken);
        await encryptor.CompleteAsync(CancellationToken.None);

        encryptor.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(output.ToArray())));
    }

    [Fact]
    public async Task A_changed_byte_is_detected()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(3 * Chunk));
        file[^3] ^= 0x01;

        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(file));
    }

    [Fact]
    public async Task A_file_cut_short_is_detected()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(3 * Chunk));

        // Without its last chunk, and cut inside a chunk.
        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(file[..^FrameLength(Chunk)]));
        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(file[..^5]));
    }

    [Fact]
    public async Task Data_after_the_last_chunk_is_detected()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(Chunk + 1));

        var ex = await Should.ThrowAsync<CryptographicException>(() => DecryptAsync([.. file, 0x00]));
        ex.Message.ShouldContain("after its end");
    }

    [Fact]
    public async Task Reordered_chunks_are_detected()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(3 * Chunk));
        var frame = FrameLength(Chunk);
        var first = BackupEncryption.HeaderSize;
        var swapped = file.ToArray();
        file.AsSpan(first, frame).CopyTo(swapped.AsSpan(first + frame));
        file.AsSpan(first + frame, frame).CopyTo(swapped.AsSpan(first));

        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(swapped));
    }

    [Fact]
    public async Task An_archive_relabelled_as_an_old_backup_does_not_decrypt()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(Chunk));
        "HC360BK1"u8.CopyTo(file);

        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(file));
    }

    [Fact]
    public async Task The_wrong_key_does_not_decrypt()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(Chunk));

        await Should.ThrowAsync<CryptographicException>(async () =>
        {
            await using var payload = await BackupEncryption.OpenAsync(new MemoryStream(file), RandomNumberGenerator.GetBytes(32), CancellationToken.None);
            await payload.CopyToAsync(Stream.Null);
        });
    }

    [Fact]
    public async Task A_backup_interrupted_before_it_completed_never_decrypts()
    {
        var output = new MemoryStream();
        await using (var encryptor = NewEncryptor(output))
        {
            await encryptor.WriteAsync(RandomNumberGenerator.GetBytes(3 * Chunk), TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(output.ToArray()));
    }

    [Fact]
    public async Task A_file_that_is_not_a_backup_is_refused()
    {
        var ex = await Should.ThrowAsync<CryptographicException>(() => DecryptAsync("PGDMP not encrypted"u8.ToArray()));
        ex.Message.ShouldContain("Not an HC360 backup");
    }

    private static int FrameLength(int length) => BackupEncryption.NonceSize + 5 + BackupEncryption.TagSize + length;

    private static BackupEncryptor NewEncryptor(Stream output) =>
        new(output, Key, BackupEncryption.ArchiveVersion, BackupEncryption.Header(BackupEncryption.ArchiveVersion, Chunk), leaveOpen: true);

    private static async Task<byte[]> EncryptAsync(byte[] payload)
    {
        var output = new MemoryStream();
        await using var encryptor = NewEncryptor(output);
        await encryptor.WriteAsync(payload);
        await encryptor.CompleteAsync(CancellationToken.None);
        return output.ToArray();
    }

    private static async Task<(int Version, byte[] Plain)> DecryptAsync(byte[] file)
    {
        var plain = new MemoryStream();
        var version = await BackupEncryption.DecryptAsync(new MemoryStream(file), plain, Key, CancellationToken.None);
        return (version, plain.ToArray());
    }

    /// <summary>The writer HC360 used before archive backups (version 1), kept here to prove old backups still restore.</summary>
    private static class PreviousVersionWriter
    {
        public static byte[] Encrypt(byte[] input, byte[] key, int chunkSize)
        {
            var output = new MemoryStream();
            output.Write("HC360BK1"u8);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, chunkSize);
            output.Write(header);

            using var aes = new AesGcm(key, 16);
            var chunks = input.Chunk(chunkSize).ToList();
            if (chunks.Count == 0)
            {
                chunks.Add([]);
            }

            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var isFinal = index == chunks.Count - 1;
                var nonce = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                var cipher = new byte[chunk.Length];
                var associated = new byte[9];
                BinaryPrimitives.WriteInt64BigEndian(associated, index);
                associated[8] = isFinal ? (byte)1 : (byte)0;
                aes.Encrypt(nonce, chunk, cipher, tag, associated);

                var length = new byte[5];
                BinaryPrimitives.WriteInt32BigEndian(length, chunk.Length);
                length[4] = associated[8];
                output.Write(nonce);
                output.Write(length);
                output.Write(tag);
                output.Write(cipher);
            }

            return output.ToArray();
        }
    }
}
