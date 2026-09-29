using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class BackupArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task An_archive_carries_the_database_the_key_and_the_documents()
    {
        var source = Machine("source");
        var key = RandomNumberGenerator.GetBytes(32);
        var dump = WriteDump(source);
        WriteDocument(source, "2026/09/a.bin", 10);
        WriteDocument(source, "2026/10/b.bin", 20);
        var documents = BackupArchive.ListDocuments(source.Files);

        var payload = new MemoryStream();
        await BackupArchive.WriteAsync(payload, Manifest(key, documents), key, dump, source.Files, documents, CancellationToken.None);
        payload.Position = 0;

        var target = Machine("target");
        var dumpOut = Path.Combine(target.Temp, "restored.dump");
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(dumpOut, target.Files), CancellationToken.None);

        contents.MasterKey.ShouldBe(key);
        contents.DocumentCount.ShouldBe(2);
        contents.Manifest.Migrations.ShouldBe(["20260928102924_InitialCreate"]);
        File.ReadAllBytes(dumpOut).ShouldBe(File.ReadAllBytes(dump));
        File.ReadAllBytes(Path.Combine(target.Files, "2026", "10", "b.bin")).ShouldBe(File.ReadAllBytes(Path.Combine(source.Files, "2026", "10", "b.bin")));
    }

    [Fact]
    public void Documents_are_listed_with_forward_slashes_in_a_stable_order()
    {
        var machine = Machine("m");
        WriteDocument(machine, "2026/10/b.bin", 3);
        WriteDocument(machine, "2026/09/a.bin", 5);

        BackupArchive.ListDocuments(machine.Files).ShouldBe([new BackupDocument("2026/09/a.bin", 5), new BackupDocument("2026/10/b.bin", 3)]);
        BackupArchive.ListDocuments(Path.Combine(_root, "missing")).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_archive_missing_a_document_its_manifest_lists_is_refused()
    {
        var source = Machine("source");
        var key = RandomNumberGenerator.GetBytes(32);
        var dump = WriteDump(source);
        WriteDocument(source, "2026/09/a.bin", 10);
        var documents = BackupArchive.ListDocuments(source.Files);

        var payload = new MemoryStream();
        await BackupArchive.WriteAsync(payload, Manifest(key, documents) with { DocumentCount = 2 }, key, dump, source.Files, documents, CancellationToken.None);
        payload.Position = 0;

        var ex = await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None));
        ex.Message.ShouldContain("manifest lists 2");
    }

    [Fact]
    public async Task A_key_that_does_not_match_the_manifest_is_refused()
    {
        var source = Machine("source");
        var key = RandomNumberGenerator.GetBytes(32);
        var dump = WriteDump(source);

        var payload = new MemoryStream();
        await BackupArchive.WriteAsync(payload, Manifest(RandomNumberGenerator.GetBytes(32), []), key, dump, source.Files, [], CancellationToken.None);
        payload.Position = 0;

        var ex = await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None));
        ex.Message.ShouldContain("does not match its manifest");
    }

    [Theory]
    [InlineData("files/../../keys/field-master.key")]
    [InlineData("files//2026/a.bin")]
    [InlineData("files/C:/Windows/a.bin")]
    [InlineData("config/hc360.settings.json")]
    public async Task An_entry_that_could_write_outside_the_restore_folders_is_refused(string name)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = new MemoryStream();
        await using (var tar = new TarWriter(payload, TarEntryFormat.Pax, leaveOpen: true))
        {
            await WriteEntryAsync(tar, BackupArchive.ManifestEntry, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Manifest(key, []), new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            await WriteEntryAsync(tar, BackupArchive.KeyEntry, key);
            await WriteEntryAsync(tar, BackupArchive.DatabaseEntry, "PGDMP"u8.ToArray());
            await WriteEntryAsync(tar, name, [1, 2, 3]);
        }

        payload.Position = 0;
        var target = Machine("target");

        await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(payload, new BackupReadTargets(null, target.Files), CancellationToken.None));
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("2026/09/0199b0c4d4b17c3e9d2f2a1b3c4d5e6f.bin", true)]
    [InlineData("a.bin", true)]
    [InlineData("../a.bin", false)]
    [InlineData("2026/./a.bin", false)]
    [InlineData("/2026/a.bin", false)]
    [InlineData("2026\\a.bin", false)]
    [InlineData("2026/a b.bin", false)]
    [InlineData("", false)]
    public void Only_the_file_stores_own_relative_paths_are_safe(string path, bool safe) =>
        BackupArchive.IsSafeRelativePath(path).ShouldBe(safe);

    [Fact]
    public async Task A_backup_restores_on_another_machine_and_its_encrypted_data_can_be_read_there()
    {
        // The old server: an encrypted identifier in the database and an encrypted document on disk.
        var oldServer = Machine("old");
        var oldProtector = new FieldProtector(oldServer, new EphemeralDataProtectionProvider());
        var abha = oldProtector.Protect("12-3456-7890-1234");
        var document = RandomNumberGenerator.GetBytes(64);
        Directory.CreateDirectory(Path.Combine(oldServer.Files, "2026", "09"));
        File.WriteAllBytes(Path.Combine(oldServer.Files, "2026", "09", "scan.bin"), oldProtector.ProtectBytes(document, "files"));
        var (backup, recoveryKey) = await BackupFileAsync(oldServer, oldProtector.ExportMasterKey());

        // Replacement hardware: its own Data Protection keys (machine DPAPI differs), nothing from the old keys folder.
        var newServer = Machine("new");
        var newProtection = new EphemeralDataProtectionProvider();
        var dumpOut = Path.Combine(newServer.Temp, "restore.dump");
        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, dumpOut, newServer, newProtection, TimeProvider.System, CancellationToken.None);

        result.KeyInstalled.ShouldBeTrue();
        result.DocumentsRestored.ShouldBe(1);
        result.PreviousKeyKeptAs.ShouldBeNull();
        File.ReadAllBytes(dumpOut)[..5].ShouldBe("PGDMP"u8.ToArray());

        var newProtector = new FieldProtector(newServer, newProtection);
        newProtector.Unprotect(abha).ShouldBe("12-3456-7890-1234");
        newProtector.UnprotectBytes(File.ReadAllBytes(Path.Combine(newServer.Files, "2026", "09", "scan.bin")), "files").ShouldBe(document);
    }

    [Fact]
    public async Task A_different_key_already_on_the_machine_is_kept_under_a_new_name()
    {
        var oldServer = Machine("old");
        var (backup, recoveryKey) = await BackupFileAsync(oldServer, RandomNumberGenerator.GetBytes(32));
        var newServer = Machine("new");
        var protection = new EphemeralDataProtectionProvider();
        var keyFromInstall = new FieldProtector(newServer, protection).ExportMasterKey();

        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer, protection, TimeProvider.System, CancellationToken.None);

        result.PreviousKeyKeptAs.ShouldNotBeNull();
        FieldMasterKey.Load(Path.Combine(newServer.Keys, result.PreviousKeyKeptAs), FieldMasterKey.CreateProtector(protection)).ShouldBe(keyFromInstall);
    }

    [Fact]
    public async Task A_damaged_backup_changes_nothing()
    {
        var oldServer = Machine("old");
        WriteDocument(oldServer, "2026/09/a.bin", 50);
        var (backup, recoveryKey) = await BackupFileAsync(oldServer, RandomNumberGenerator.GetBytes(32));
        var bytes = File.ReadAllBytes(backup);
        bytes[^10] ^= 0x01;
        File.WriteAllBytes(backup, bytes);
        var newServer = Machine("new");

        await Should.ThrowAsync<CryptographicException>(() =>
            BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer, new EphemeralDataProtectionProvider(), TimeProvider.System, CancellationToken.None));

        File.Exists(FieldMasterKey.PathIn(newServer)).ShouldBeFalse();
        Directory.EnumerateFiles(newServer.Files, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_key_that_does_not_belong_to_the_recovery_key_is_not_installed()
    {
        var oldServer = Machine("old");
        var (backup, recoveryKey) = await BackupFileAsync(oldServer, masterKey: RandomNumberGenerator.GetBytes(32), encryptWith: RandomNumberGenerator.GetBytes(32));
        var newServer = Machine("new");

        await Should.ThrowAsync<InvalidDataException>(() =>
            BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer, new EphemeralDataProtectionProvider(), TimeProvider.System, CancellationToken.None));

        File.Exists(FieldMasterKey.PathIn(newServer)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_older_backup_restores_its_database()
    {
        var recoveryKey = RandomNumberGenerator.GetBytes(32);
        var machine = Machine("m");
        var backup = Path.Combine(machine.Backups, "old.hc360bak");
        await using (var output = File.Create(backup))
        {
            await BackupEncryption.EncryptLegacyAsync(new MemoryStream("PGDMP legacy"u8.ToArray()), output, recoveryKey, CancellationToken.None);
        }

        var dumpOut = Path.Combine(machine.Temp, "r.dump");
        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, dumpOut, machine, new EphemeralDataProtectionProvider(), TimeProvider.System, CancellationToken.None);

        result.Version.ShouldBe(BackupEncryption.LegacyVersion);
        result.KeyInstalled.ShouldBeFalse();
        File.ReadAllText(dumpOut).ShouldBe("PGDMP legacy");
    }

    [Fact]
    public void A_key_fingerprint_identifies_a_key_without_revealing_it()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        var fingerprint = FieldMasterKey.Fingerprint(key);

        fingerprint.Length.ShouldBe(16);
        FieldMasterKey.Fingerprint(key).ShouldBe(fingerprint);
        FieldMasterKey.Fingerprint(RandomNumberGenerator.GetBytes(32)).ShouldNotBe(fingerprint);
        Convert.ToHexString(key).ShouldNotContain(fingerprint);
    }

    [Fact]
    public void Installing_the_key_that_is_already_there_changes_nothing()
    {
        var machine = Machine("m");
        var protection = new EphemeralDataProtectionProvider();
        var key = new FieldProtector(machine, protection).ExportMasterKey();

        FieldMasterKey.Install(FieldMasterKey.PathIn(machine), FieldMasterKey.CreateProtector(protection), key, DateTimeOffset.UtcNow).ShouldBeNull();
        Directory.GetFiles(machine.Keys, "field-master.key*").Length.ShouldBe(1);
        Should.Throw<CryptographicException>(() => FieldMasterKey.Install(FieldMasterKey.PathIn(machine), FieldMasterKey.CreateProtector(protection), new byte[16], DateTimeOffset.UtcNow));
    }

    private DataDirectories Machine(string name)
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, name) });
        directories.EnsureCreated();
        return directories;
    }

    private static string WriteDump(DataDirectories machine)
    {
        var path = Path.Combine(machine.Temp, "database.dump");
        File.WriteAllBytes(path, [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(100)]);
        return path;
    }

    private static void WriteDocument(DataDirectories machine, string relativePath, int length)
    {
        var path = Path.Combine(machine.Files, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(length));
    }

    private static BackupManifest Manifest(byte[] key, IReadOnlyList<BackupDocument> documents) =>
        new(BackupArchive.Format, "Manual", DateTimeOffset.UtcNow, "1.0.0", "hc360", ["20260928102924_InitialCreate"], true,
            documents.Count, documents.Sum(d => d.Length), FieldMasterKey.Fingerprint(key));

    /// <summary>An encrypted archive of the machine's documents, as the backup service writes it.</summary>
    private static async Task<(string File, byte[] RecoveryKey)> BackupFileAsync(DataDirectories machine, byte[] masterKey, byte[]? encryptWith = null)
    {
        var recoveryKey = FieldMasterKey.DeriveKey(encryptWith ?? masterKey, "backup");
        var documents = BackupArchive.ListDocuments(machine.Files);
        var file = Path.Combine(machine.Backups, "test.hc360bak");
        await using (var output = File.Create(file))
        await using (var encryptor = BackupEncryption.CreateEncryptor(output, recoveryKey))
        {
            await BackupArchive.WriteAsync(encryptor, Manifest(masterKey, documents), masterKey, WriteDump(machine), machine.Files, documents, CancellationToken.None);
            await encryptor.CompleteAsync(CancellationToken.None);
        }

        return (file, recoveryKey);
    }

    private static async Task WriteEntryAsync(TarWriter tar, string name, byte[] content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) };
        await tar.WriteEntryAsync(entry);
    }
}
