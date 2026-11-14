using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Format 3 archives: the documents are listed in <c>documents.idx</c> and kept in the backup folder beside the archive
/// (docs/design/incremental-backup.md, section 5.2). Format 2 archives, with the documents inside, are covered by
/// <see cref="BackupArchiveTests"/> and must keep reading.
/// </summary>
public sealed class BackupArchiveFormat3Tests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_full_backup_lists_its_documents_instead_of_carrying_them()
    {
        var machine = Machine("source");
        var key = RandomNumberGenerator.GetBytes(32);
        var dump = WriteDump(machine);
        var documents = new[] { Indexed("2026/09/a.bin", 10), Indexed("2026/10/b.bin", 20) };

        var payload = new MemoryStream();
        var written = await BackupArchive.WriteIndexedAsync(payload, Manifest(key), key, [], null, dump, documents, CancellationToken.None);
        payload.Position = 0;

        written.Format.ShouldBe(BackupArchive.Format);
        written.IncludesDocuments.ShouldBeTrue();
        written.DocumentCount.ShouldBe(2);
        written.DocumentBytes.ShouldBe(30);
        written.DocumentIndexSha256.ShouldNotBeNull();
        EntryNames(payload).ShouldBe([BackupArchive.ManifestEntry, BackupArchive.KeyEntry, BackupArchive.DocumentIndexEntry, BackupArchive.DatabaseEntry]);

        var target = Machine("target");
        var dumpOut = Path.Combine(target.Temp, "restored.dump");
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(dumpOut, target.Files), CancellationToken.None);

        contents.Manifest.Migrations.ShouldBe(written.Migrations);
        (contents.Manifest with { Migrations = written.Migrations }).ShouldBe(written);
        contents.Index.ShouldBe(documents);
        contents.DocumentCount.ShouldBe(2);
        contents.DocumentBytes.ShouldBe(30);
        File.ReadAllBytes(dumpOut).ShouldBe(File.ReadAllBytes(dump));
        Directory.EnumerateFiles(target.Files, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_backup_before_an_update_has_no_index()
    {
        var machine = Machine("m");
        var key = RandomNumberGenerator.GetBytes(32);

        var payload = new MemoryStream();
        var written = await BackupArchive.WriteIndexedAsync(payload, Manifest(key), key, [], null, WriteDump(machine), null, CancellationToken.None);
        payload.Position = 0;

        written.IncludesDocuments.ShouldBeFalse();
        written.DocumentIndexSha256.ShouldBeNull();
        EntryNames(payload).ShouldNotContain(BackupArchive.DocumentIndexEntry);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None);
        contents.Index.ShouldBeEmpty();
        contents.DocumentCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_full_backup_of_an_empty_document_store_has_an_empty_index()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = new MemoryStream();
        var written = await BackupArchive.WriteIndexedAsync(payload, Manifest(key), key, [], null, WriteDump(Machine("m")), [], CancellationToken.None);
        payload.Position = 0;

        written.IncludesDocuments.ShouldBeTrue();
        (await BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None)).Index.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_older_writer_still_writes_format_2_whatever_the_manifest_says()
    {
        var machine = Machine("m");
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(machine.Files, "2026", "09", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(10));
        var documents = BackupArchive.ListDocuments(machine.Files);

        var payload = new MemoryStream();
        await BackupArchive.WriteAsync(payload, Manifest(key) with { DocumentCount = 1, DocumentBytes = 10 }, key, [], null, WriteDump(machine), machine.Files,
            documents, CancellationToken.None);
        payload.Position = 0;

        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None);
        contents.Manifest.Format.ShouldBe(BackupArchive.InlineDocumentsFormat);
        contents.DocumentCount.ShouldBe(1);
        contents.Index.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_index_that_is_not_the_one_the_manifest_names_is_refused()
    {
        var payload = await FullBackupAsync();
        var tampered = await RewriteAsync(payload, manifest: m => m with { DocumentIndexSha256 = new string('0', 64) });

        (await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(tampered, new BackupReadTargets(), CancellationToken.None)))
            .Message.ShouldContain("does not match its manifest");
    }

    [Fact]
    public async Task A_document_count_that_does_not_match_the_index_is_refused()
    {
        var payload = await FullBackupAsync();
        var tampered = await RewriteAsync(payload, manifest: m => m with { DocumentCount = 3 });

        (await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(tampered, new BackupReadTargets(), CancellationToken.None)))
            .Message.ShouldContain("manifest lists 3");
    }

    [Fact]
    public async Task A_full_backup_without_its_index_is_refused()
    {
        var payload = await FullBackupAsync();
        var tampered = await RewriteAsync(payload, drop: BackupArchive.DocumentIndexEntry, manifest: m => m with { DocumentCount = 0, DocumentBytes = 0 });

        (await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(tampered, new BackupReadTargets(), CancellationToken.None)))
            .Message.ShouldContain("document index its manifest lists is missing");
    }

    [Fact]
    public async Task A_format_3_archive_with_documents_inside_is_refused()
    {
        var payload = await FullBackupAsync();
        var tampered = await RewriteAsync(payload, add: (BackupArchive.DocumentsPrefix + "2026/09/extra.bin", [1, 2, 3]));

        (await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(tampered, new BackupReadTargets(), CancellationToken.None)))
            .Message.ShouldContain("Unexpected entry");
    }

    [Fact]
    public async Task A_format_this_version_does_not_know_is_refused()
    {
        var payload = await FullBackupAsync();
        var tampered = await RewriteAsync(payload, manifest: m => m with { Format = BackupArchive.Format + 1 });

        (await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(tampered, new BackupReadTargets(), CancellationToken.None)))
            .Message.ShouldContain("not supported");
    }

    [Fact]
    public async Task A_format_3_backup_restores_its_documents_from_the_folder_it_is_in()
    {
        var (backup, recoveryKey, document, plain) = await BackupWithStoredDocumentAsync();
        var newServer = Machine("new");
        var protection = new EphemeralDataProtectionProvider();

        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer, protection, TimeProvider.System,
            CancellationToken.None);

        result.DocumentsRestored.ShouldBe(1);
        var restored = Path.Combine(newServer.Files, document);
        new FieldProtector(newServer, protection).UnprotectBytes(File.ReadAllBytes(restored), "files").ShouldBe(plain);
        Directory.EnumerateFiles(newServer.Temp, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("r.dump", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_its_documents_a_format_3_backup_changes_nothing()
    {
        // F16: the archive was copied without the folder beside it.
        var (backup, recoveryKey, _, _) = await BackupWithStoredDocumentAsync();
        var alone = Path.Combine(_root, "alone", Path.GetFileName(backup));
        Directory.CreateDirectory(Path.GetDirectoryName(alone)!);
        File.Copy(backup, alone);
        var newServer = Machine("new");

        var refused = await Should.ThrowAsync<InvalidDataException>(() => BackupRestore.RestoreAsync(alone, recoveryKey, Path.Combine(newServer.Temp, "r.dump"),
            newServer, new EphemeralDataProtectionProvider(), TimeProvider.System, CancellationToken.None));

        refused.Message.ShouldContain("1 of the backup's 1 documents have no good copy");
        refused.Message.ShouldContain("copy the whole folder");
        File.Exists(FieldMasterKey.PathIn(newServer)).ShouldBeFalse();
        Directory.EnumerateFiles(newServer.Files, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_documents_can_be_restored_from_a_folder_named_apart_from_the_archive()
    {
        var (backup, recoveryKey, document, _) = await BackupWithStoredDocumentAsync();
        var alone = Path.Combine(_root, "alone", Path.GetFileName(backup));
        Directory.CreateDirectory(Path.GetDirectoryName(alone)!);
        File.Copy(backup, alone);
        var newServer = Machine("new");

        await BackupRestore.RestoreAsync(alone, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer, new EphemeralDataProtectionProvider(),
            TimeProvider.System, CancellationToken.None, blobsFolder: Path.GetDirectoryName(backup));

        File.Exists(Path.Combine(newServer.Files, document)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_damaged_copy_is_passed_over_for_a_good_one_beside_it_and_none_good_changes_nothing()
    {
        // F23: on an append-only share a copy cut short stays, and the next run writes another beside it.
        var (backup, recoveryKey, document, _) = await BackupWithStoredDocumentAsync();
        var folder = Path.GetDirectoryName(backup)!;
        var blob = Directory.EnumerateFiles(Path.Combine(folder, BackupStore.BlobsFolder), "*.blob", SearchOption.AllDirectories).Single();
        var good = File.ReadAllBytes(blob);
        File.WriteAllBytes(Path.ChangeExtension(blob, null) + ".1.blob", good);
        var damaged = good.ToArray();
        damaged[^1] ^= 1;
        File.WriteAllBytes(blob, damaged);

        var first = Machine("first");
        (await BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(first.Temp, "r.dump"), first, new EphemeralDataProtectionProvider(), TimeProvider.System,
            CancellationToken.None)).DocumentsRestored.ShouldBe(1);
        File.ReadAllBytes(Path.Combine(first.Files, document)).ShouldBe(good);

        File.WriteAllBytes(Path.ChangeExtension(blob, null) + ".1.blob", damaged);
        var second = Machine("second");
        (await Should.ThrowAsync<InvalidDataException>(() => BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(second.Temp, "r.dump"), second,
            new EphemeralDataProtectionProvider(), TimeProvider.System, CancellationToken.None))).Message.ShouldContain("every copy found is damaged");
        File.Exists(FieldMasterKey.PathIn(second)).ShouldBeFalse();
    }

    [Fact]
    public async Task Documents_are_found_in_the_generations_of_an_append_only_destination()
    {
        var (backup, recoveryKey, document, _) = await BackupWithStoredDocumentAsync();
        var folder = Path.GetDirectoryName(backup)!;
        Directory.CreateDirectory(Path.Combine(folder, "gen-20270111"));
        Directory.Move(Path.Combine(folder, BackupStore.BlobsFolder), Path.Combine(folder, "gen-20270111", BackupStore.BlobsFolder));
        var newServer = Machine("new");

        await BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer, new EphemeralDataProtectionProvider(),
            TimeProvider.System, CancellationToken.None);

        File.Exists(Path.Combine(newServer.Files, document)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_document_encrypted_with_a_key_the_backup_does_not_hold_is_refused()
    {
        // F9: every document a backup lists must be readable with a key it escrows.
        var oldServer = Machine("old");
        var masterKey = new FieldProtector(oldServer, new EphemeralDataProtectionProvider()).ExportMasterKey();
        var path = Path.Combine(oldServer.Files, "2027", "01", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [2, .. RandomNumberGenerator.GetBytes(8), .. RandomNumberGenerator.GetBytes(60)]);
        var (backup, recoveryKey) = await WriteBackupAsync(oldServer, masterKey, [path]);
        var newServer = Machine("new");

        (await Should.ThrowAsync<InvalidDataException>(() => BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer,
            new EphemeralDataProtectionProvider(), TimeProvider.System, CancellationToken.None))).Message.ShouldContain("which the backup does not hold");
    }

    /// <summary>
    /// A machine with one document stored encrypted, backed up as the backup service does: the document in the backup
    /// folder (<see cref="BackupStore"/>), and a format 3 archive beside it listing it.
    /// </summary>
    private async Task<(string Backup, byte[] RecoveryKey, string Document, byte[] Plain)> BackupWithStoredDocumentAsync()
    {
        var oldServer = Machine("old");
        var protector = new FieldProtector(oldServer, new EphemeralDataProtectionProvider());
        var plain = RandomNumberGenerator.GetBytes(64);
        var path = Path.Combine(oldServer.Files, "2027", "01", "scan.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, protector.ProtectBytes(plain, "files"));
        var (backup, recoveryKey) = await WriteBackupAsync(oldServer, protector.ExportMasterKey(), [path]);
        return (backup, recoveryKey, Path.Combine("2027", "01", "scan.bin"), plain);
    }

    private static async Task<(string Backup, byte[] RecoveryKey)> WriteBackupAsync(DataDirectories machine, byte[] masterKey, IReadOnlyList<string> documents)
    {
        var referencesKey = FieldMasterKey.DeriveKey(masterKey, BackupStore.ReferencesKeyPurpose);
        var store = BackupStore.Open(machine.Backups, FieldMasterKey.Fingerprint(masterKey), referencesKey, TimeProvider.System);
        var index = new List<BackupIndexedDocument>();
        using (var held = store.Lock())
        {
            foreach (var path in documents)
            {
                var id = await BackupStore.HashFileAsync(path, CancellationToken.None);
                await store.AddAsync(held, path, id, CancellationToken.None);
                index.Add(new BackupIndexedDocument(Path.GetRelativePath(machine.Files, path).Replace('\\', '/'), id, new FileInfo(path).Length));
            }
        }

        var recoveryKey = BackupRecoveryKey.Original(masterKey);
        var file = Path.Combine(machine.Backups, "hc360-20270115-013000-scheduled.hc360bak");
        await using (var output = File.Create(file))
        await using (var encryptor = BackupEncryption.CreateEncryptor(output, recoveryKey))
        {
            await BackupArchive.WriteIndexedAsync(encryptor, Manifest(masterKey), masterKey, [], null, WriteDump(machine), index, CancellationToken.None);
            await encryptor.CompleteAsync(CancellationToken.None);
        }

        return (file, recoveryKey);
    }

    [Fact]
    public async Task The_index_reads_back_and_refuses_what_is_not_a_stored_document()
    {
        var documents = new[] { Indexed("2026/09/a.bin", 1), Indexed("2027/01/b.bin", 25 * 1024 * 1024) };
        var stream = new MemoryStream();
        var sha = BackupDocumentIndex.Write(stream, documents);
        sha.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(stream.ToArray())));

        stream.Position = 0;
        var (read, readSha) = await BackupDocumentIndex.ReadAsync(stream, CancellationToken.None);
        read.ShouldBe(documents);
        readSha.ShouldBe(sha);

        Should.Throw<ArgumentException>(() => BackupDocumentIndex.Write(new MemoryStream(), [Indexed("../outside.bin", 1)]));
        Should.Throw<ArgumentException>(() => BackupDocumentIndex.Write(new MemoryStream(), [Indexed("C:/x.bin", 1)]));
        Should.Throw<ArgumentException>(() => BackupDocumentIndex.Write(new MemoryStream(), [Indexed("2026/09/a.bin", 1), Indexed("2026/09/a.bin", 2)]));
        Should.Throw<ArgumentException>(() => BackupDocumentIndex.Write(new MemoryStream(), [new BackupIndexedDocument("2026/09/a.bin", "not-a-hash", 1)]));

        var truncated = new MemoryStream(stream.ToArray()[..^8]);
        await Should.ThrowAsync<InvalidDataException>(() => BackupDocumentIndex.ReadAsync(truncated, CancellationToken.None));
        await Should.ThrowAsync<InvalidDataException>(() => BackupDocumentIndex.ReadAsync(new MemoryStream("not an index"u8.ToArray()), CancellationToken.None));
    }

    [Fact]
    public void The_index_is_about_sixty_bytes_a_document()
    {
        // The sizing in the design (section 2.2) assumes about 60 bytes a document: file store paths compress well; the
        // SHA-256 does not compress at all.
        var documents = Enumerable.Range(0, 10_000)
            .Select(i => Indexed($"2027/{(i % 12) + 1:00}/{Guid.CreateVersion7():N}.bin", 50_000 + i))
            .ToList();
        var stream = new MemoryStream();
        BackupDocumentIndex.Write(stream, documents);

        (stream.Length / (double)documents.Count).ShouldBeLessThan(70);
    }

    private async Task<MemoryStream> FullBackupAsync()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = new MemoryStream();
        await BackupArchive.WriteIndexedAsync(payload, Manifest(key), key, [], null, WriteDump(Machine(Guid.NewGuid().ToString("N"))),
            [Indexed("2026/09/a.bin", 10), Indexed("2026/10/b.bin", 20)], CancellationToken.None);
        payload.Position = 0;
        return payload;
    }

    /// <summary>The same archive with the manifest changed, an entry dropped or one added (the payload before encryption).</summary>
    private static async Task<MemoryStream> RewriteAsync(
        MemoryStream payload, Func<BackupManifest, BackupManifest>? manifest = null, string? drop = null, (string Name, byte[] Content)? add = null)
    {
        var output = new MemoryStream();
        await using (var reader = new TarReader(payload, leaveOpen: true))
        await using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
            {
                if (entry.Name == drop)
                {
                    continue;
                }

                var content = new MemoryStream();
                await entry.DataStream!.CopyToAsync(content);
                var bytes = content.ToArray();
                if (entry.Name == BackupArchive.ManifestEntry && manifest is not null)
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(manifest(JsonSerializer.Deserialize<BackupManifest>(bytes, Json)!), Json);
                }

                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = new MemoryStream(bytes) });
                if (entry.Name == BackupArchive.ManifestEntry && add is { } extra)
                {
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, extra.Name) { DataStream = new MemoryStream(extra.Content) });
                }
            }
        }

        output.Position = 0;
        return output;
    }

    private static List<string> EntryNames(MemoryStream payload)
    {
        var names = new List<string>();
        using (var reader = new TarReader(payload, leaveOpen: true))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                names.Add(entry.Name);
            }
        }

        payload.Position = 0;
        return names;
    }

    private static BackupIndexedDocument Indexed(string path, long length) =>
        new(path, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))), length);

    private static BackupManifest Manifest(byte[] key) =>
        new(BackupArchive.Format, "Manual", DateTimeOffset.UtcNow, "1.0.0", "hc360", ["20260928102924_InitialCreate"], true, 0, 0, FieldMasterKey.Fingerprint(key));

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
}
