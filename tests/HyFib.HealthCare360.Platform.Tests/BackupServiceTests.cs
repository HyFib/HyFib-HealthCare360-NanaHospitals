using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePostgres _postgres = new();
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly Auditing.AuditSealKey _sealKey;
    private readonly FieldProtector _protector;
    private readonly RecoveryKeyStore _recoveryKeys;

    public BackupServiceTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _options.Backup.SecondaryDirectory = Path.Combine(_root, "nas");
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        var protection = new EphemeralDataProtectionProvider();
        _protector = new FieldProtector(_directories, protection);
        _recoveryKeys = new RecoveryKeyStore(_directories, protection, _protector, TimeProvider.System);
        _sealKey = new Auditing.AuditSealKey(_directories, protection);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task The_backup_before_an_update_is_a_verified_archive_of_the_database_and_the_key()
    {
        var result = await Service().CreatePreMigrationAsync(["20260928102924_InitialCreate"], CancellationToken.None);

        result.Kind.ShouldBe(BackupKind.PreMigration);
        result.DocumentCount.ShouldBeNull();
        result.Warning.ShouldBeNull();
        result.SecondaryCopied.ShouldBeTrue();
        var file = Path.Combine(_directories.Backups, result.FileName);
        result.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        File.ReadAllBytes(Path.Combine(_options.Backup.SecondaryDirectory!, result.FileName)).ShouldBe(File.ReadAllBytes(file));

        // pg_restore was given the very dump that pg_dump wrote.
        _postgres.Validated.ShouldBe(_postgres.Dumped);

        await using var input = File.OpenRead(file);
        await using var payload = await BackupEncryption.OpenAsync(input, _protector.DeriveKey("backup"), CancellationToken.None);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None);
        contents.Manifest.Kind.ShouldBe("PreMigration");
        contents.Manifest.IncludesDocuments.ShouldBeFalse();
        contents.Manifest.Migrations.ShouldBe(["20260928102924_InitialCreate"]);
        contents.Manifest.Database.ShouldBe("hc360_test");
        contents.MasterKey.ShouldBe(_protector.ExportMasterKey());

        // No plaintext dump or unfinished file is left behind.
        Directory.EnumerateFiles(_directories.Temp).ShouldBeEmpty();
        Directory.EnumerateFiles(_directories.Backups, "*.partial").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_document_saved_while_the_database_is_dumped_is_in_the_backup()
    {
        // The file store writes a document, then commits its row. A row that made it into the dump must find its file in
        // the backup, so the documents are listed after the dump.
        var document = Path.Combine(_directories.Files, "2026", "09", "saved-during-dump.bin");
        _postgres.DuringDump = () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(document)!);
            File.WriteAllBytes(document, RandomNumberGenerator.GetBytes(40));
        };

        var ct = TestContext.Current.CancellationToken;
        var result = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow, includeDocuments: true, [], ct);

        result.DocumentCount.ShouldBe(1);
        var restored = Path.Combine(_root, "restored");
        await using var input = File.OpenRead(Path.Combine(_directories.Backups, result.FileName));
        await using var payload = await BackupEncryption.OpenAsync(input, _protector.DeriveKey("backup"), ct);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(DocumentsFolder: restored), ct);
        contents.Manifest.DocumentCount.ShouldBe(1);
        File.ReadAllBytes(Path.Combine(restored, "2026", "09", "saved-during-dump.bin")).ShouldBe(File.ReadAllBytes(document));
    }

    [Fact]
    public async Task A_backup_names_the_recovery_key_that_opens_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = BackupRecoveryKey.Original(_protector.ExportMasterKey());

        var result = await Service().CreatePreMigrationAsync([], ct);

        result.RecoveryKeyId.ShouldBe(BackupRecoveryKey.Id(original));
        var file = Path.Combine(_directories.Backups, result.FileName);
        await using (var input = File.OpenRead(file))
        await using (var payload = await BackupEncryption.OpenAsync(input, original, ct))
        {
            (payload.Version, payload.KeyId).ShouldBe((BackupEncryption.KeyedVersion, result.RecoveryKeyId));
        }

        // Another key is turned away before anything is decrypted, with the id of the key that opens the backup.
        var other = BackupRecoveryKey.Create();
        await using var again = File.OpenRead(file);
        var ex = await Should.ThrowAsync<BackupKeyMismatchException>(() => BackupEncryption.OpenAsync(again, other, ct));
        (ex.BackupKeyId, ex.GivenKeyId).ShouldBe((result.RecoveryKeyId, BackupRecoveryKey.Id(other)));
    }

    [Fact]
    public async Task After_a_new_recovery_key_backups_open_with_it_and_earlier_ones_with_the_key_before()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = BackupRecoveryKey.Original(_protector.ExportMasterKey());
        var before = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow.AddMinutes(-5), includeDocuments: true, [], ct);
        var admin = Guid.NewGuid();
        var created = _recoveryKeys.Create(admin);
        _recoveryKeys.Confirm(created.KeyId, created.Key, admin).Value.ShouldBe(before.RecoveryKeyId);
        BackupRecoveryKey.TryParse(created.Key, out var newKey, out _).ShouldBeTrue();

        var after = await Service().CreateAsync(BackupKind.Manual, DateTimeOffset.UtcNow, includeDocuments: true, [], ct);

        after.RecoveryKeyId.ShouldBe(created.KeyId);
        await OpensWithAsync(after, newKey);
        await OpensWithAsync(before, original);
        await using var input = File.OpenRead(Path.Combine(_directories.Backups, after.FileName));
        await Should.ThrowAsync<BackupKeyMismatchException>(() => BackupEncryption.OpenAsync(input, original, ct));
    }

    [Fact]
    public async Task A_backup_is_refused_when_the_disk_has_no_room_for_it()
    {
        _postgres.DatabaseSize = long.MaxValue / 4;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service().CreatePreMigrationAsync([], CancellationToken.None));

        ex.Message.ShouldContain("Not enough free disk space");
        _postgres.Dumped.ShouldBeNull();
        Directory.EnumerateFiles(_directories.Backups).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_backup_whose_dump_cannot_be_read_back_is_discarded()
    {
        _postgres.DumpIsDamaged = true;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service().CreatePreMigrationAsync([], CancellationToken.None));

        ex.Message.ShouldContain("pg_restore cannot read");
        Directory.EnumerateFiles(_directories.Backups).ShouldBeEmpty();
        Directory.EnumerateFiles(_directories.Temp).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_pg_restore_the_backup_is_kept_with_a_note()
    {
        _postgres.HasPgRestore = false;

        var result = await Service().CreatePreMigrationAsync([], CancellationToken.None);

        result.Warning.ShouldNotBeNull().ShouldContain("without pg_restore");
        File.Exists(Path.Combine(_directories.Backups, result.FileName)).ShouldBeTrue();
    }

    private async Task OpensWithAsync(BackupResult backup, byte[] key)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var input = File.OpenRead(Path.Combine(_directories.Backups, backup.FileName));
        await using var payload = await BackupEncryption.OpenAsync(input, key, ct);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), ct);
        contents.MasterKey.ShouldBe(_protector.ExportMasterKey());
    }

    private BackupService Service() => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _recoveryKeys,
        _protector,
        _sealKey,
        new NoDocumentsLock(),
        _postgres,
        Substitute.For<INotificationService>(),
        TimeProvider.System,
        NullLogger<BackupService>.Instance);

    private sealed class FakePostgres : IPostgresTools
    {
        public long DatabaseSize { get; set; } = 1024;

        public bool DumpIsDamaged { get; set; }

        public bool HasPgRestore { get; set; } = true;

        public byte[]? Dumped { get; private set; }

        public byte[]? Validated { get; private set; }

        /// <summary>Something that happens on the server while pg_dump runs.</summary>
        public Action? DuringDump { get; set; }

        public string DatabaseName => "hc360_test";

        public Task<long> GetDatabaseSizeAsync(CancellationToken ct) => Task.FromResult(DatabaseSize);

        public async Task DumpAsync(string outputFile, CancellationToken ct)
        {
            DuringDump?.Invoke();
            Dumped = [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(4096)];
            await File.WriteAllBytesAsync(outputFile, Dumped, ct);
        }

        public async Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct)
        {
            Validated = await File.ReadAllBytesAsync(dumpFile, ct);
            return DumpIsDamaged
                ? throw new InvalidOperationException("pg_restore cannot read the database dump (exit code 1): input file does not appear to be a valid archive")
                : HasPgRestore;
        }
    }

    private sealed class NoDocumentsLock : IDocumentStoreLock
    {
        public Task<IAsyncDisposable> HoldSharedAsync(CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(new Released());

        private sealed class Released : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
