using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS REL-02: the WAL PostgreSQL archives never leaves the server unencrypted. HC360 encrypts each file with the backup
/// recovery key into its backup folder within a minute, checks it opens to the same bytes before it removes the plain one,
/// copies only the encrypted files (and the base backups) to the secondary directory, the disk taken off site, and keeps
/// them for as long as the base backups they replay onto. The restore tool opens them again with the recovery key.
/// </summary>
public sealed class WalArchiveTests : IDisposable
{
    private const string SystemId = "7412345678901234567";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 22, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly RecoveryKeyStore _recoveryKeys;
    private readonly string _archive;
    private readonly string _sealed;
    private readonly string _secondary;

    public WalArchiveTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _secondary = Path.Combine(_root, "usb-disk");
        _options.Backup.SecondaryDirectory = _secondary;
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        var protection = new EphemeralDataProtectionProvider();
        _recoveryKeys = new RecoveryKeyStore(_directories, protection, new FieldProtector(_directories, protection), TimeProvider.System);
        _archive = Path.Combine(_root, "wal-archive");
        Directory.CreateDirectory(_archive);
        _sealed = PointInTimeRecovery.WalFolder(_directories.Backups, SystemId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_archive_folder_is_read_from_the_archive_command_Enable_HC360Replication_writes()
    {
        WalArchive.ArchiveFolder(Command(@"C:\ProgramData\HyFib\HealthCare360\wal-archive")).ShouldBe(@"C:\ProgramData\HyFib\HealthCare360\wal-archive");
        WalArchive.ArchiveFolder(Command(@"E:\HC360 WAL")).ShouldBe(@"E:\HC360 WAL");
        WalArchive.ArchiveFolder(@"copy ""%p"" ""E:\wal\%f""").ShouldBeNull("an archive set up by hand is left alone");
        WalArchive.ArchiveFolder(Command(@"C:\a").Replace(@"move /y ""C:\a\%f.tmp""", @"move /y ""C:\b\%f.tmp""", StringComparison.Ordinal)).ShouldBeNull();
    }

    [Theory]
    [InlineData("000000010000000000000042", true)]
    [InlineData("00000002.history", true)]
    [InlineData("000000010000000000000042.00000028.backup", true)]
    [InlineData("000000010000000000000042.partial", true)]
    [InlineData("000000010000000000000042.tmp", false)]
    [InlineData("00000001000000000000004a", false)]
    [InlineData("archive_status", false)]
    public void Only_the_files_PostgreSQL_archives_are_taken(string name, bool taken) => WalArchive.IsArchivedName(name).ShouldBe(taken);

    [Fact]
    public async Task A_WAL_file_is_encrypted_compressed_with_the_recovery_key_and_opens_to_the_same_bytes_under_its_own_name_only()
    {
        // PostgreSQL closes a file every quarter of an hour even when little was written: mostly zeros.
        var content = new byte[16 * 1024 * 1024];
        RandomNumberGenerator.Fill(content.AsSpan(0, 4096));
        var plain = Plain("000000010000000000000042", content);
        var sealedFile = Path.Combine(_root, "000000010000000000000042.hc360wal");
        var key = _recoveryKeys.Current().Key;

        await WalArchive.SealAsync(plain, sealedFile, key, TestContext.Current.CancellationToken);

        new FileInfo(sealedFile).Length.ShouldBeLessThan(256 * 1024, "compressed before it is encrypted");
        using var opened = new MemoryStream();
        await WalArchive.OpenAsync(sealedFile, key, "000000010000000000000042", opened, TestContext.Current.CancellationToken);
        opened.ToArray().ShouldBe(content);

        await Should.ThrowAsync<InvalidDataException>(() =>
            WalArchive.OpenAsync(sealedFile, key, "000000010000000000000043", Stream.Null, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<BackupKeyMismatchException>(() =>
            WalArchive.OpenAsync(sealedFile, BackupRecoveryKey.Create(), "000000010000000000000042", Stream.Null, TestContext.Current.CancellationToken));

        var bytes = await File.ReadAllBytesAsync(sealedFile, TestContext.Current.CancellationToken);
        bytes[^30] ^= 0x01;
        await File.WriteAllBytesAsync(sealedFile, bytes, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<CryptographicException>(() =>
            WalArchive.OpenAsync(sealedFile, key, "000000010000000000000042", Stream.Null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_job_encrypts_each_archived_file_removes_the_plain_one_and_copies_only_the_encrypted_one_off_the_server()
    {
        var first = Plain("000000010000000000000042", RandomNumberGenerator.GetBytes(8192));
        var history = Plain("00000002.history", Encoding.ASCII.GetBytes("1\t0/42000000\tno recovery target specified\n"));
        var copying = Plain("000000010000000000000043.tmp", RandomNumberGenerator.GetBytes(100));
        var service = Service();

        var status = await service.SealAsync(_archive, _sealed, new PointInTimeRecoveryStatus(), TestContext.Current.CancellationToken);
        status = service.Copy(_secondary, SystemId, status);

        File.Exists(first).ShouldBeFalse("the plain file goes once its encrypted copy opens to the same bytes");
        File.Exists(history).ShouldBeFalse();
        File.Exists(copying).ShouldBeTrue("PostgreSQL's copy under a temporary name is not a WAL file");
        Directory.EnumerateFiles(_sealed).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe(["000000010000000000000042.hc360wal", "00000002.history.hc360wal"]);
        (status.WalSealedAt, status.WalSealed, status.WalError).ShouldBe((Now, "00000002.history", null));

        var copies = PointInTimeRecovery.WalFolder(_secondary, SystemId);
        foreach (var file in Directory.EnumerateFiles(_sealed))
        {
            (await File.ReadAllBytesAsync(Path.Combine(copies, Path.GetFileName(file)), TestContext.Current.CancellationToken))
                .ShouldBe(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        }

        Directory.EnumerateFiles(_secondary, "*", SearchOption.AllDirectories).ShouldAllBe(f => f.EndsWith(".hc360wal", StringComparison.Ordinal), "nothing unencrypted reaches the secondary directory");
        (status.CopiedAt, status.CopyFailingSince).ShouldBe((Now, null));
    }

    [Fact]
    public async Task A_file_archived_again_with_the_same_bytes_counts_as_archived_and_one_with_other_bytes_is_kept_aside()
    {
        var content = RandomNumberGenerator.GetBytes(8192);
        Plain("000000010000000000000042", content);
        var service = Service();
        await service.SealAsync(_archive, _sealed, new PointInTimeRecoveryStatus(), TestContext.Current.CancellationToken);
        var sealedFile = Path.Combine(_sealed, "000000010000000000000042.hc360wal");
        var kept = await File.ReadAllBytesAsync(sealedFile, TestContext.Current.CancellationToken);

        // PostgreSQL archives a file again after a crash before it recorded the first time.
        var again = Plain("000000010000000000000042", content);
        var status = await service.SealAsync(_archive, _sealed, new PointInTimeRecoveryStatus(), TestContext.Current.CancellationToken);
        File.Exists(again).ShouldBeFalse();
        status.WalConflict.ShouldBeNull();

        var other = Plain("000000010000000000000042", RandomNumberGenerator.GetBytes(8192));
        status = await service.SealAsync(_archive, _sealed, new PointInTimeRecoveryStatus(), TestContext.Current.CancellationToken);
        File.Exists(other).ShouldBeTrue("other bytes under the same name stay unencrypted, for a person to look at");
        status.WalConflict.ShouldBe("000000010000000000000042");
        (await File.ReadAllBytesAsync(sealedFile, TestContext.Current.CancellationToken)).ShouldBe(kept, "the file kept is never replaced");
    }

    [Fact]
    public async Task A_copy_cut_short_is_copied_again_and_copies_that_fail_say_since_when()
    {
        Plain("000000010000000000000042", RandomNumberGenerator.GetBytes(8192));
        var service = Service();
        var status = await service.SealAsync(_archive, _sealed, new PointInTimeRecoveryStatus(), TestContext.Current.CancellationToken);
        var copy = Path.Combine(PointInTimeRecovery.WalFolder(_secondary, SystemId), "000000010000000000000042.hc360wal");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await File.WriteAllBytesAsync(copy, [1, 2, 3], TestContext.Current.CancellationToken);

        service.Copy(_secondary, SystemId, status);
        (await File.ReadAllBytesAsync(copy, TestContext.Current.CancellationToken))
            .ShouldBe(await File.ReadAllBytesAsync(Path.Combine(_sealed, "000000010000000000000042.hc360wal"), TestContext.Current.CancellationToken));

        // The USB disk is unplugged: its drive letter is a file here, so nothing can be made under it.
        var unplugged = Path.Combine(_root, "unplugged");
        await File.WriteAllTextAsync(unplugged, string.Empty, TestContext.Current.CancellationToken);
        var failing = service.Copy(Path.Combine(unplugged, "HC360-Backups"), SystemId, status with { CopiedAt = Now.AddDays(-1) });
        (failing.CopyFailingSince, failing.CopiedAt).ShouldBe((Now, Now.AddDays(-1)));
        failing.CopyError.ShouldNotBeNullOrWhiteSpace();
        service.Copy(Path.Combine(unplugged, "HC360-Backups"), SystemId, failing with { CopyFailingSince = Now.AddHours(-3) })
            .CopyFailingSince.ShouldBe(Now.AddHours(-3), "since the first copy that failed");
        service.Copy(_secondary, SystemId, failing).CopyFailingSince.ShouldBeNull();
    }

    [Fact]
    public void The_encrypted_WAL_older_than_the_oldest_base_backup_kept_goes_here_and_in_the_secondary_directory()
    {
        _options.Backup.PointInTimeDays = 30;
        var bases = PointInTimeRecovery.BaseBackupFolder(_directories.Backups, SystemId);
        Directory.CreateDirectory(bases);
        File.WriteAllBytes(Path.Combine(bases, BaseBackupArchive.FileName(Now.AddDays(-20), "000000010000000000000040")), []);
        File.WriteAllBytes(Path.Combine(bases, BaseBackupArchive.FileName(Now.AddDays(-6), "000000020000000000000060")), []);
        var secondaryBases = PointInTimeRecovery.BaseBackupFolder(_secondary, SystemId);
        Directory.CreateDirectory(secondaryBases);
        File.WriteAllBytes(Path.Combine(secondaryBases, BaseBackupArchive.FileName(Now.AddDays(-45), "000000010000000000000020")), []);
        File.WriteAllBytes(Path.Combine(secondaryBases, BaseBackupArchive.FileName(Now.AddDays(-20), "000000010000000000000040")), []);
        string[] names = ["00000001000000000000003F", "000000010000000000000040", "00000001000000000000004F.partial", "000000020000000000000050", "00000002.history"];
        foreach (var root in new[] { _directories.Backups, _secondary })
        {
            var folder = PointInTimeRecovery.WalFolder(root, SystemId);
            Directory.CreateDirectory(folder);
            foreach (var name in names)
            {
                File.WriteAllBytes(Path.Combine(folder, name + WalArchive.FileExtension), []);
            }
        }

        Service().RemoveExpired(SystemId, _secondary);

        foreach (var root in new[] { _directories.Backups, _secondary })
        {
            Directory.EnumerateFiles(PointInTimeRecovery.WalFolder(root, SystemId)).Select(f => Path.GetFileName(f)[..^WalArchive.FileExtension.Length]).Order(StringComparer.Ordinal).ToList()
                .ShouldBe(["000000010000000000000040", "00000001000000000000004F.partial", "00000002.history", "000000020000000000000050"], customMessage: root);
        }

        BaseBackupArchive.List(secondaryBases).Select(f => f.StartWalFile).ShouldBe(["000000010000000000000040"], "the secondary copies past their days go too");
    }

    [Fact]
    public void Without_a_base_backup_no_encrypted_WAL_is_removed()
    {
        WalArchive.Expired(["00000001000000000000003F"], "not a WAL file").ShouldBeEmpty();
        Directory.CreateDirectory(_sealed);
        File.WriteAllBytes(Path.Combine(_sealed, "00000001000000000000003F.hc360wal"), []);

        Service().RemoveExpired(SystemId, _secondary);

        File.Exists(Path.Combine(_sealed, "00000001000000000000003F.hc360wal")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_restore_tool_decrypts_a_folder_with_each_recovery_key_in_turn()
    {
        var first = RandomNumberGenerator.GetBytes(8192);
        var second = RandomNumberGenerator.GetBytes(8192);
        var keyA = BackupRecoveryKey.Create();
        var keyB = BackupRecoveryKey.Create();
        Directory.CreateDirectory(_sealed);
        await WalArchive.SealAsync(Plain("000000010000000000000042", first), Path.Combine(_sealed, "000000010000000000000042.hc360wal"), keyA, TestContext.Current.CancellationToken);
        await WalArchive.SealAsync(Plain("000000010000000000000043", second), Path.Combine(_sealed, "000000010000000000000043.hc360wal"), keyB, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "restore-wal");

        var withA = await WalArchive.DecryptFolderAsync(_sealed, keyA, output, TestContext.Current.CancellationToken);
        withA.Decrypted.ShouldBe(1);
        withA.OtherKeys.ShouldBe(new Dictionary<string, int> { [BackupRecoveryKey.Id(keyB)] = 1 });

        var withB = await WalArchive.DecryptFolderAsync(_sealed, keyB, output, TestContext.Current.CancellationToken);
        (withB.Decrypted, withB.OtherKeys.Count).ShouldBe((1, 0), "the file opened before is left as it is");
        (await File.ReadAllBytesAsync(Path.Combine(output, "000000010000000000000042"), TestContext.Current.CancellationToken)).ShouldBe(first);
        (await File.ReadAllBytesAsync(Path.Combine(output, "000000010000000000000043"), TestContext.Current.CancellationToken)).ShouldBe(second);
        Directory.EnumerateFiles(output, "*.partial").ShouldBeEmpty();
    }

    [Fact]
    public void System_health_says_critically_when_archived_WAL_stays_unencrypted_on_the_servers_disk()
    {
        SystemHealthArchive.Warnings(Report(oldestPlain: Now.AddMinutes(-2)), Now).ShouldBeEmpty("HC360 encrypts it every minute");

        var stuck = SystemHealthArchive.Warnings(Report(oldestPlain: Now.AddMinutes(-20), status: new() { WalError = "Access to the path is denied." }), Now).ShouldHaveSingleItem();
        (stuck.Code, stuck.Severity).ShouldBe(("pitr.wal_not_encrypted", "critical"));
        stuck.Message.ShouldContain("Access to the path is denied.");
    }

    [Fact]
    public void System_health_says_critically_when_a_WAL_file_was_archived_again_with_other_content()
    {
        var warning = SystemHealthArchive.Warnings(Report(status: new() { WalConflict = "000000010000000000000042" }), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("pitr.wal_conflict", "critical"));
        warning.Message.ShouldContain("000000010000000000000042");
        warning.Message.ShouldContain(_archive);
    }

    [Fact]
    public void System_health_warns_while_the_copies_off_the_server_fail_and_turns_critical_after_three_and_a_half_days()
    {
        var failing = new PointInTimeRecoveryStatus { CopyFailingSince = Now.AddHours(-5), CopyError = "The device is not ready." };
        var warning = SystemHealthArchive.Warnings(Report(status: failing), Now).ShouldHaveSingleItem();
        (warning.Code, warning.Severity).ShouldBe(("pitr.wal_not_copied", "warning"));
        warning.Message.ShouldContain(_secondary);
        warning.Message.ShouldContain("The device is not ready.");

        SystemHealthArchive.Warnings(Report(status: failing with { CopyFailingSince = Now.AddDays(-4) }), Now).ShouldHaveSingleItem().Severity.ShouldBe("critical");
        SystemHealthArchive.Warnings(Report(status: failing) with { Secondary = null }, Now).ShouldBeEmpty("there is nowhere to copy them");
    }

    private ArchiveReport Report(DateTimeOffset? oldestPlain = null, PointInTimeRecoveryStatus? status = null) =>
        new(true, new DatabaseCluster(SystemId, "000000010000000000000060"), new BaseBackupFile("x", Now.AddDays(-1), "000000010000000000000042"),
            status ?? new PointInTimeRecoveryStatus(), _archive, oldestPlain, _secondary);

    /// <summary>A WAL file PostgreSQL archived a minute ago.</summary>
    private string Plain(string name, byte[] content)
    {
        var path = Path.Combine(_archive, name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime - TimeSpan.FromMinutes(1));
        return path;
    }

    /// <summary>The archive_command Enable-HC360Replication.ps1's Get-ArchiveCommand writes for <paramref name="folder"/>.</summary>
    private static string Command(string folder) =>
        $"""if exist "{folder}\%f" (fc /b "%p" "{folder}\%f" >nul) else (copy /b /y "%p" "{folder}\%f.tmp" >nul && move /y "{folder}\%f.tmp" "{folder}\%f" >nul)""";

    private WalArchiveService Service() => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _recoveryKeys,
        new FixedTime(Now),
        NullLogger<WalArchiveService>.Instance);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
