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

/// <summary>
/// Model-based tests of the incremental backups (docs/design/incremental-backup.md, implementation step 5): seeded random
/// sequences of what happens to a hospital's server over weeks (nightly backups, documents added, moved to a new key and
/// removed, recovery keys rotated, two USB disks swapped and unplugged, backups stopped at any step as a crash would, with
/// what a power cut leaves behind) checked after every step against what each backup must hold:
/// <list type="bullet">
/// <item><b>Restorable alone.</b> Every archive in every folder restores from that folder alone, to exactly the documents
/// the server had when it was made. The model remembers those itself; it does not trust the archive's index.</item>
/// <item><b>Nothing kept past its time.</b> After a backup that finished, a folder HC360 may delete from holds exactly the
/// documents its remaining archives list, and no archive older than the backup period but its newest (F4, F14).</item>
/// </list>
/// A failure names the seed and the steps before it, so the sequence can be run again.
/// </summary>
public sealed class BackupModelTests : IDisposable
{
    private const int RetentionDays = 30;
    private const int Steps = 110;

    private static readonly string[] CrashPoints =
    [
        "dumped", "document copied", "archive written", "archive verified", "list written", "archive in place",
        "secondary document copied", "secondary list written", "before prune",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly Dictionary<string, Archive> _archives = new(StringComparer.Ordinal);
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly FieldProtector _protector;
    private readonly RecoveryKeyStore _recoveryKeys;
    private readonly Auditing.AuditSealKey _sealKey;
    private readonly ManualTime _clock = new(new DateTimeOffset(2027, 1, 4, 1, 30, 0, TimeSpan.Zero));
    private readonly string _unplugged;
    private Random _random = new(0);
    private string? _mounted;

    public BackupModelTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _options.Backup.RetentionDays = RetentionDays;
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        var protection = new EphemeralDataProtectionProvider();
        _protector = new FieldProtector(_directories, protection);
        _recoveryKeys = new RecoveryKeyStore(_directories, protection, _protector, TimeProvider.System);
        _sealKey = new Auditing.AuditSealKey(_directories, protection);
        _unplugged = Path.Combine(_root, "no-disk");
        File.WriteAllText(_unplugged, string.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static TheoryData<int, bool> Sequences()
    {
        var data = new TheoryData<int, bool>();
        foreach (var seed in Enumerable.Range(1, 8))
        {
            data.Add(seed, false);
        }

        foreach (var seed in Enumerable.Range(101, 8))
        {
            data.Add(seed, true);
        }

        return data;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DiskA => Path.Combine(_root, "disk-a");

    private string DiskB => Path.Combine(_root, "disk-b");

    private IEnumerable<string> Folders => new[] { _directories.Backups, DiskA, DiskB }.Where(Directory.Exists);

    [Theory]
    [MemberData(nameof(Sequences))]
    public async Task Random_sequences_keep_every_backup_restorable_from_each_folder_alone(int seed, bool appendOnlyDisks)
    {
        _random = new Random(seed);
        _options.Backup.SecondaryAppendOnly = appendOnlyDisks;
        Mount(DiskA);
        AddDocuments(5);
        var nights = 0;
        try
        {
            for (var step = 0; step < Steps; step++)
            {
                var roll = _random.Next(100);
                var backedUp = roll < 50;
                switch (roll)
                {
                    case < 45:
                        // The next night at 01:30.
                        _clock.Now = new DateTimeOffset(_clock.Now.UtcDateTime.Date.AddDays(1).AddHours(1).AddMinutes(30), TimeSpan.Zero);
                        if (await BackUpAsync(crash: _random.Next(100) < (appendOnlyDisks ? 30 : 15)))
                        {
                            nights++;
                        }

                        break;
                    case < 50:
                        _clock.Now = _clock.Now.AddMinutes(37);
                        await BackUpAsync(crash: false);
                        break;
                    case < 70:
                        AddDocuments(_random.Next(1, 4));
                        break;
                    case < 78:
                        MoveToNewKey();
                        break;
                    case < 85:
                        RemoveDocument();
                        break;
                    case < 87:
                        RotateRecoveryKey();
                        break;
                    case < 94:
                        Mount(_mounted == DiskA ? DiskB : DiskA);
                        break;
                    default:
                        Mount(null);
                        break;
                }

                // Only a backup (or one stopped as a crash would) changes what the folders hold.
                if (backedUp)
                {
                    await CheckRestorableAsync();
                }
            }
        }
        catch (Exception ex) when (ex is not ModelFailure)
        {
            throw new ModelFailure($"Seed {seed} (append-only disks: {appendOnlyDisks}) failed: {ex.Message}\nLast steps:\n{string.Join("\n", _log.TakeLast(30))}", ex);
        }

        // The sequence must have exercised what it is for.
        nights.ShouldBeGreaterThan(30, "enough nights for backups to expire");
        _archives.Count.ShouldBeGreaterThan(0);
        _log.ShouldContain(l => l.StartsWith("crash", StringComparison.Ordinal));
    }

    /// <summary>A backup run; when <paramref name="crash"/>, stopped at a random step, and sometimes with what a power cut leaves.</summary>
    private async Task<bool> BackUpAsync(bool crash)
    {
        var startedAt = _clock.Now;
        var name = $"hc360-{startedAt:yyyyMMdd-HHmmss}-scheduled{BackupService.FileExtension}";
        var snapshot = BackupArchive.ListDocuments(_directories.Files)
            .ToDictionary(d => d.RelativePath, d => Sha256(Path.Combine(_directories.Files, d.RelativePath)), StringComparer.Ordinal);
        var current = _recoveryKeys.Current();
        _archives[name] = new Archive(snapshot, current.Key.ToArray());

        var point = crash ? CrashPoints[_random.Next(CrashPoints.Length)] : null;
        var occurrence = _random.Next(1, 4);
        var seen = 0;
        var service = Service(step =>
        {
            if (step == point && ++seen == occurrence)
            {
                throw new CrashException(step);
            }
        });

        try
        {
            var result = await service.CreateAsync(BackupKind.Scheduled, startedAt, includeDocuments: true, [], Ct);
            _log.Add($"backup {name}: {snapshot.Count} documents, second copy {(result.SecondaryCopied ? "made" : "not made")}{(result.Warning is { } w ? $" ({w})" : "")}");
            CheckNothingKeptPastItsTime(_directories.Backups);
            if (result.SecondaryCopied && _mounted is { } disk && !_options.Backup.SecondaryAppendOnly)
            {
                CheckNothingKeptPastItsTime(disk);
            }

            return true;
        }
        catch (CrashException ex)
        {
            _log.Add($"crash {name} at {ex.Message} (#{occurrence})");
            if (_options.Backup.SecondaryAppendOnly || _random.Next(2) == 0)
            {
                LeavePowerCutDebris(name);
            }

            return false;
        }
    }

    /// <summary>What a power cut leaves that a stopped process does not: a document half copied, an unfinished archive, a list without its archive.</summary>
    private void LeavePowerCutDebris(string archive)
    {
        foreach (var folder in new[] { _directories.Backups, _mounted }.OfType<string>().Where(Directory.Exists))
        {
            var appendOnly = File.Exists(Path.Combine(folder, BackupStore.AppendOnlyFlagFile));
            if (appendOnly)
            {
                // Copies there are written under their own name: one cut short stays, and must be passed over.
                // A document the generation does not have yet is the one a crash could have been copying.
                var generation = BackupStore.CurrentGeneration(folder, _clock.Now);
                var missing = BackupArchive.ListDocuments(_directories.Files)
                    .Select(d => (d.RelativePath, Id: Sha256(Path.Combine(_directories.Files, d.RelativePath))))
                    .Where(d => !File.Exists(Path.Combine(folder, generation, BackupStore.BlobsFolder, d.Id[..2], d.Id[2..4], d.Id + BackupStore.BlobExtension)))
                    .ToList();
                if (missing.Count > 0)
                {
                    var (document, id) = missing[_random.Next(missing.Count)];
                    var path = Path.Combine(folder, generation, BackupStore.BlobsFolder, id[..2], id[2..4], id + BackupStore.BlobExtension);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(_directories.Files, document))[..1]);
                    _log.Add($"  power cut left {id[..8]} cut short in {generation} of {Path.GetFileName(folder)}");
                }

                continue;
            }

            var half = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var temp = Path.Combine(folder, BackupStore.BlobsFolder, half[..2], half[2..4], $"{half}.blob.{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            File.WriteAllBytes(temp, [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(folder, archive + ".partial"), [4, 5, 6]);

            // A list is written whole under another name and renamed, before its archive is: a power cut can leave a list
            // whose archive never arrived, never an archive in place with a damaged list.
            if (!File.Exists(Path.Combine(folder, archive)))
            {
                File.WriteAllText(Path.Combine(folder, Path.ChangeExtension(archive, ".refs")), "half a list");
            }

            _log.Add($"  power cut left a .tmp, a .partial and perhaps a list in {Path.GetFileName(folder)}");
        }
    }

    /// <summary>Every archive in every folder restores from that folder alone, to the documents the server had when it was made.</summary>
    private async Task CheckRestorableAsync()
    {
        foreach (var folder in Folders)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*" + BackupService.FileExtension))
            {
                var name = Path.GetFileName(file);
                var archive = _archives.GetValueOrDefault(name) ?? throw new ModelFailure($"{name} in {Path.GetFileName(folder)} is no backup the model made");
                BackupContents contents;
                await using (var input = File.OpenRead(file))
                await using (var payload = await BackupEncryption.OpenAsync(input, archive.RecoveryKey, Ct))
                {
                    contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), Ct);
                }

                var listed = contents.Index.ToDictionary(d => d.RelativePath, d => d.BlobId, StringComparer.Ordinal);
                if (!listed.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(archive.Documents.OrderBy(p => p.Key, StringComparer.Ordinal)))
                {
                    throw new ModelFailure($"{name} in {Path.GetFileName(folder)} lists {listed.Count} documents; the server had {archive.Documents.Count} when it was made");
                }

                var problems = await BackupBlobs.CheckAsync(folder, contents, Ct);
                if (problems.Count > 0)
                {
                    throw new ModelFailure($"{name} does not restore from {Path.GetFileName(folder)} alone: {string.Join("; ", problems)}");
                }
            }
        }
    }

    /// <summary>After a backup that finished, the folder holds what its archives need and no more, and no archive past its time.</summary>
    private void CheckNothingKeptPastItsTime(string folder)
    {
        var archives = Directory.EnumerateFiles(folder, "*" + BackupService.FileExtension).Select(Path.GetFileName).OfType<string>().ToList();
        var cutoff = _clock.Now - TimeSpan.FromDays(RetentionDays);
        var newest = archives.Max(a => BackupStore.ArchiveDate(a));
        var expired = archives.Where(a => BackupStore.ArchiveDate(a) < cutoff && BackupStore.ArchiveDate(a) != newest).ToList();
        if (expired.Count > 0)
        {
            throw new ModelFailure($"{Path.GetFileName(folder)} still has {string.Join(", ", expired)}, past the backup period");
        }

        var needed = archives.SelectMany(a => _archives[a].Documents.Values).ToHashSet(StringComparer.Ordinal);
        var held = Directory.Exists(Path.Combine(folder, BackupStore.BlobsFolder))
            ? Directory.EnumerateFiles(Path.Combine(folder, BackupStore.BlobsFolder), "*", SearchOption.AllDirectories).ToList()
            : [];
        var kept = held.Select(f => Path.GetFileName(f)).Where(n => !n.EndsWith(BackupStore.BlobExtension, StringComparison.Ordinal)).ToList();
        if (kept.Count > 0)
        {
            throw new ModelFailure($"{Path.GetFileName(folder)} still has leftovers: {string.Join(", ", kept)}");
        }

        var ids = held.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (!ids.SetEquals(needed))
        {
            throw new ModelFailure($"{Path.GetFileName(folder)} holds {ids.Count} documents; its archives need {needed.Count} " +
                $"({ids.Except(needed).Count()} unneeded, {needed.Except(ids).Count()} missing)");
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".partial", StringComparison.Ordinal) || fileName.EndsWith(".tmp", StringComparison.Ordinal)
                || (fileName.EndsWith(BackupStore.ReferencesExtension, StringComparison.Ordinal) && !archives.Contains(Path.ChangeExtension(fileName, BackupService.FileExtension))))
            {
                throw new ModelFailure($"{Path.GetFileName(folder)} still has {fileName} after a backup that finished");
            }
        }
    }

    private void AddDocuments(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var relative = $"{_clock.Now:yyyy}/{_clock.Now:MM}/{Guid.NewGuid():N}.bin";
            var path = Path.Combine(_directories.Files, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, _protector.ProtectBytes(RandomNumberGenerator.GetBytes(_random.Next(100, 20_000)), "files"));
            _log.Add($"add {relative}");
        }
    }

    /// <summary>A move to a new key: the document written again under the same name, as FieldKeyMover does.</summary>
    private void MoveToNewKey()
    {
        if (RandomDocument() is not { } document)
        {
            return;
        }

        var path = Path.Combine(_directories.Files, document);
        var plain = _protector.UnprotectBytes(File.ReadAllBytes(path), "files");
        File.WriteAllBytes(path + ".moving", _protector.ProtectBytes(plain, "files"));
        File.Move(path + ".moving", path, overwrite: true);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(_random.Next(1, 100)));
        _log.Add($"move {document} to a new key");
    }

    private void RemoveDocument()
    {
        if (RandomDocument() is not { } document)
        {
            return;
        }

        File.Delete(Path.Combine(_directories.Files, document));
        _log.Add($"remove {document}");
    }

    private void RotateRecoveryKey()
    {
        var admin = Guid.NewGuid();
        var created = _recoveryKeys.Create(admin);
        _recoveryKeys.Confirm(created.KeyId, created.Key, admin).IsSuccess.ShouldBeTrue();
        _log.Add($"new recovery key {created.KeyId}");
    }

    private void Mount(string? disk)
    {
        _mounted = disk;
        _options.Backup.SecondaryDirectory = disk ?? Path.Combine(_unplugged, "HC360-Backups");
        _log.Add(disk is null ? "unplug the disk" : $"plug in {Path.GetFileName(disk)}");
    }

    private string? RandomDocument()
    {
        var documents = BackupArchive.ListDocuments(_directories.Files);
        return documents.Count == 0 ? null : documents[_random.Next(documents.Count)].RelativePath;
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private BackupService Service(Action<string> checkpoint) => new(
        Substitute.For<IDbContextFactory<HospitalDbContext>>(),
        Options.Create(_options),
        _directories,
        _recoveryKeys,
        _protector,
        _sealKey,
        new NoDocumentsLock(),
        new FakePostgres(),
        Substitute.For<INotificationService>(),
        _clock,
        NullLogger<BackupService>.Instance)
    {
        Checkpoint = checkpoint,
    };

    /// <summary>The documents (storage path → SHA-256 of the stored file) the server had when a backup was made, and the recovery key it is made with.</summary>
    private sealed record Archive(IReadOnlyDictionary<string, string> Documents, byte[] RecoveryKey);

    private sealed class CrashException(string step) : Exception(step);

    private sealed class ModelFailure : Exception
    {
        public ModelFailure(string message)
            : base(message)
        {
        }

        public ModelFailure(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakePostgres : IPostgresTools
    {
        public string DatabaseName => "hc360_test";

        public Task<long> GetDatabaseSizeAsync(CancellationToken ct) => Task.FromResult(1024L);

        public Task DumpAsync(string outputFile, CancellationToken ct) =>
            File.WriteAllBytesAsync(outputFile, [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(512)], ct);

        public Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct) => Task.FromResult(true);
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
