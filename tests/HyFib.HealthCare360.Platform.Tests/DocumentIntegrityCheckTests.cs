using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The restore drill's check of the stored documents: each one the database records must be in place, decrypt with the
/// keys on the machine, and hold what was saved. It must name every kind of failure, and never count one as good.
/// </summary>
public sealed class DocumentIntegrityCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly DataDirectories _directories;
    private readonly FieldProtector _protector;

    public DocumentIntegrityCheckTests()
    {
        _directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        _directories.EnsureCreated();
        _protector = new FieldProtector(_directories, new EphemeralDataProtectionProvider());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DocumentRecord Store(byte[] content, string? recordedHash = null, byte[]? onDisk = null)
    {
        var record = new DocumentRecord(Guid.NewGuid(), $"2027/01/{Guid.NewGuid():N}.bin", recordedHash ?? Convert.ToHexString(SHA256.HashData(content)));
        var path = Path.Combine(_directories.Files, record.StoragePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, onDisk ?? _protector.ProtectBytes(content, "files"));
        return record;
    }

    private Task<DocumentCheckResult> CheckAsync(params DocumentRecord[] documents) =>
        DocumentIntegrityCheck.CheckAsync(documents.ToAsyncEnumerable(), _directories.Files, _protector, CancellationToken.None);

    [Fact]
    public async Task Documents_in_place_that_decrypt_to_what_was_saved_pass()
    {
        var result = await CheckAsync(Store(RandomNumberGenerator.GetBytes(100)), Store(RandomNumberGenerator.GetBytes(5)));

        result.ShouldBe(result with { Checked = 2, Missing = 0, Unreadable = 0, Mismatched = 0, WithoutHash = 0 });
        result.Ok.ShouldBeTrue();
        result.Problems.ShouldBeEmpty();
        DocumentIntegrityCheck.Summary(result).ShouldBe("Documents checked: 2; missing: 0; unreadable: 0; not as saved: 0; without a recorded hash: 0.");
    }

    [Fact]
    public async Task A_missing_file_an_unreadable_one_and_one_not_as_saved_each_fail_and_are_named()
    {
        var good = Store(RandomNumberGenerator.GetBytes(50));
        var missing = Store(RandomNumberGenerator.GetBytes(50));
        File.Delete(Path.Combine(_directories.Files, missing.StoragePath));
        var otherKey = new FieldProtector(new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, "other") }), new EphemeralDataProtectionProvider());
        var content = RandomNumberGenerator.GetBytes(50);
        var unreadable = Store(content, onDisk: otherKey.ProtectBytes(content, "files"));
        var changed = Store(RandomNumberGenerator.GetBytes(50), recordedHash: Convert.ToHexString(SHA256.HashData([1, 2, 3])));

        var result = await CheckAsync(good, missing, unreadable, changed);

        (result.Checked, result.Missing, result.Unreadable, result.Mismatched).ShouldBe((4, 1, 1, 1));
        result.Ok.ShouldBeFalse();
        result.Problems.Count.ShouldBe(3);
        result.Problems.ShouldContain(p => p.StartsWith(missing.Id.ToString(), StringComparison.Ordinal) && p.EndsWith("the file is missing", StringComparison.Ordinal));
        result.Problems.ShouldContain(p => p.StartsWith(unreadable.Id.ToString(), StringComparison.Ordinal) && p.Contains("does not decrypt", StringComparison.Ordinal));
        result.Problems.ShouldContain(p => p.StartsWith(changed.Id.ToString(), StringComparison.Ordinal) && p.EndsWith("not what was saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_saved_without_a_hash_is_counted_apart_and_does_not_fail_the_check()
    {
        var result = await CheckAsync(Store(RandomNumberGenerator.GetBytes(20), recordedHash: ""));

        (result.Checked, result.WithoutHash, result.Ok).ShouldBe((1, 1, true));
    }

    [Fact]
    public async Task The_recorded_hash_is_compared_whatever_its_letter_case()
    {
        var content = RandomNumberGenerator.GetBytes(30);

        (await CheckAsync(Store(content, recordedHash: Convert.ToHexStringLower(SHA256.HashData(content))))).Ok.ShouldBeTrue();
    }
}
