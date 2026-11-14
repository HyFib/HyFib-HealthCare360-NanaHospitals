using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The cache that spares a nightly backup from reading every document. A stale entry would make a backup list a
/// document under its earlier bytes, so an entry must be used only while the file is exactly as it was.
/// </summary>
public sealed class BackupHashCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public BackupHashCacheTests() => Directory.CreateDirectory(Path.Combine(_root, "files", "2027", "01"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Files => Path.Combine(_root, "files");

    private BackupDocument Write(string relative, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(Files, relative), content);
        return new BackupDocument(relative, content.Length);
    }

    [Fact]
    public async Task A_hash_is_used_again_only_while_the_file_is_unchanged()
    {
        var cache = BackupHashCache.Empty();
        var document = Write("2027/01/a.bin", RandomNumberGenerator.GetBytes(100));
        var first = await cache.HashAsync(Files, document, CancellationToken.None);
        first.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Files, document.RelativePath)))));
        cache.Lookup(document.RelativePath, new FileInfo(Path.Combine(Files, document.RelativePath))).ShouldBe(first);

        // Rewritten with the same length, as a move to a new key does: a new file renamed into place.
        var path = Path.Combine(Files, document.RelativePath);
        File.WriteAllBytes(path + ".new", RandomNumberGenerator.GetBytes(100));
        File.Move(path + ".new", path, overwrite: true);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

        cache.Lookup(document.RelativePath, new FileInfo(path)).ShouldBeNull();
        var second = await cache.HashAsync(Files, document, CancellationToken.None);
        second.ShouldNotBe(first);
        second.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public async Task The_cache_reads_back_and_forgets_documents_no_longer_stored()
    {
        var cache = BackupHashCache.Empty();
        var kept = Write("2027/01/kept.bin", RandomNumberGenerator.GetBytes(50));
        var gone = Write("2027/01/gone.bin", RandomNumberGenerator.GetBytes(50));
        var keptHash = await cache.HashAsync(Files, kept, CancellationToken.None);
        await cache.HashAsync(Files, gone, CancellationToken.None);
        cache.KeepOnly(new HashSet<string>(StringComparer.Ordinal) { kept.RelativePath });
        var saved = Path.Combine(_root, BackupHashCache.FileName);
        cache.Save(saved);

        var loaded = BackupHashCache.Load(saved);

        loaded.Count.ShouldBe(1);
        loaded.Lookup(kept.RelativePath, new FileInfo(Path.Combine(Files, kept.RelativePath))).ShouldBe(keptHash);
        Directory.EnumerateFiles(_root, "*.tmp").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_or_damaged_cache_is_an_empty_one(bool damaged)
    {
        var path = Path.Combine(_root, BackupHashCache.FileName);
        if (damaged)
        {
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(200));
        }

        BackupHashCache.Load(path).Count.ShouldBe(0);
    }
}
