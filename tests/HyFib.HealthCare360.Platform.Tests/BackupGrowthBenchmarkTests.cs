using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// How the backup folders grow over 60 nights (docs/design/incremental-backup.md, section 2.2 and implementation step 6),
/// measured on real backup runs: documents added each night, the data key rotated on night 20 (every document written
/// again), the backup period reached on night 31. The model the sizing in DEPLOYMENT.md and NANA-THENI-READINESS.md rests on:
/// <code>size ≈ R × (D + I) + Docs + Docs_R</code>
/// where R is the backups kept, D + I a backup's archive and list, Docs the documents now, and Docs_R the earlier versions
/// of documents that kept backups still need. The table of each night goes to the test output.
/// </summary>
public sealed class BackupGrowthBenchmarkTests : IDisposable
{
    private const int Nights = 60;
    private const int RetentionDays = 30;
    private const int RotationNight = 20;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private readonly FieldProtector _protector;
    private readonly RecoveryKeyStore _recoveryKeys;
    private readonly Auditing.AuditSealKey _sealKey;
    private readonly ManualTime _clock = new(new DateTimeOffset(2027, 1, 1, 1, 30, 0, TimeSpan.Zero));
    private readonly Random _random = new(2027);
    private readonly Dictionary<string, IReadOnlyDictionary<string, long>> _made = new(StringComparer.Ordinal);

    public BackupGrowthBenchmarkTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        // Every backup of the newest 31 nights that have one (tonight's and the 30 before), and nothing older:
        // grandfather-father-son with no weekly or monthly backups, the period this benchmark was measured with.
        _options.Backup.Retention = new BackupRetentionOptions { Daily = RetentionDays + 1, Weekly = 0, Monthly = 0 };
        _options.Backup.SecondaryDirectory = Path.Combine(_root, "usb");
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sixty_nights_grow_by_the_new_documents_and_one_backup_a_night_and_match_the_sizing_model()
    {
        var report = new StringBuilder();
        report.AppendLine("night | archives | documents now (kB) | earlier versions kept (kB) | server folder (kB) | model (kB) | every document in every backup (kB)");
        long lastBefore = 0;
        long lastAfter = 0;
        for (var night = 1; night <= Nights; night++)
        {
            _clock.Now = _clock.Now.AddDays(1);
            AddDocuments(_random.Next(2, 5));
            if (night == RotationNight)
            {
                MoveAllToNewKey();
            }

            var result = await Service().CreateAsync(BackupKind.Scheduled, _clock.Now, includeDocuments: true, [], Ct);
            result.SecondaryCopied.ShouldBeTrue();
            result.Warning.ShouldBeNull();
            _made[result.FileName] = CurrentDocuments();

            foreach (var folder in new[] { _directories.Backups, _options.Backup.SecondaryDirectory! })
            {
                var measured = Measure(folder);

                // Exactly what the kept backups need: their archives and lists, one copy of each document version they
                // list, and the folder's marker, lock, (on the server) hash cache and (on the disk) the disk's id. Nothing left over.
                measured.Total.ShouldBe(measured.Archives + measured.NeededDocuments + measured.Overhead,
                    $"night {night}, {Path.GetFileName(folder)}: the folder holds something no kept backup needs");

                // The sizing formula, with the average archive and list for D + I.
                var model = (measured.ArchiveCount * (measured.Archives / (double)measured.ArchiveCount)) + measured.DocumentsNow + measured.EarlierVersions;
                ((double)measured.Total).ShouldBeInRange(model * 0.98, (model * 1.02) + measured.Overhead);
            }

            var server = Measure(_directories.Backups);
            var before = server.ArchiveCount * (server.DocumentsNow + (server.Archives / server.ArchiveCount));
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{night,5} | {server.ArchiveCount,8} | {server.DocumentsNow / 1024,18} | {server.EarlierVersions / 1024,26} | {server.Total / 1024,18} | {(server.Archives + server.DocumentsNow + server.EarlierVersions + server.Overhead) / 1024,10} | {before / 1024,35}"));

            // The backup period: 31 backups kept once it is reached (tonight's and the 30 days before).
            server.ArchiveCount.ShouldBe(Math.Min(night, RetentionDays + 1));

            // The earlier versions are kept while a backup made before the rotation is: until night 49, when night 19's is
            // the oldest kept. Then they go.
            if (night is >= RotationNight and < RotationNight + RetentionDays)
            {
                server.EarlierVersions.ShouldBeGreaterThan(0);
            }
            else
            {
                server.EarlierVersions.ShouldBe(0);
            }

            lastBefore = before;
            lastAfter = server.Total;
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        // Night 60: one copy of each document and 31 small archives, against 31 copies of everything.
        (lastBefore / (double)lastAfter).ShouldBeGreaterThan(10);
    }

    private (long Total, long Archives, int ArchiveCount, long NeededDocuments, long DocumentsNow, long EarlierVersions, long Overhead) Measure(string folder)
    {
        var archives = Directory.EnumerateFiles(folder, "*" + BackupService.FileExtension).Select(Path.GetFileName).OfType<string>().ToList();
        var archiveBytes = archives.Sum(a => new FileInfo(Path.Combine(folder, a)).Length + new FileInfo(Path.Combine(folder, Path.ChangeExtension(a, ".refs"))).Length);
        var needed = archives.SelectMany(a => _made[a]).GroupBy(d => d.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var now = CurrentDocuments();
        var overhead = new[] { BackupStore.MarkerFileName, BackupStore.LockFileName, BackupHashCache.FileName, BackupDisk.MarkerFileName }
            .Select(f => Path.Combine(folder, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length);
        var total = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return (total, archiveBytes, archives.Count, needed.Values.Sum(), now.Values.Sum(),
            needed.Where(d => !now.ContainsKey(d.Key)).Sum(d => d.Value), overhead);
    }

    /// <summary>Each stored document version now: SHA-256 of the stored file → its length.</summary>
    private Dictionary<string, long> CurrentDocuments() =>
        BackupArchive.ListDocuments(_directories.Files)
            .Select(d => Path.Combine(_directories.Files, d.RelativePath))
            .ToDictionary(p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))), p => new FileInfo(p).Length, StringComparer.Ordinal);

    private void AddDocuments(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_directories.Files, $"{_clock.Now:yyyy}/{_clock.Now:MM}/{Guid.NewGuid():N}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, _protector.ProtectBytes(RandomNumberGenerator.GetBytes(_random.Next(5_000, 40_000)), "files"));
        }
    }

    /// <summary>What moving to a new data key does to the documents: each written again, under the same name.</summary>
    private void MoveAllToNewKey()
    {
        foreach (var document in BackupArchive.ListDocuments(_directories.Files))
        {
            var path = Path.Combine(_directories.Files, document.RelativePath);
            File.WriteAllBytes(path + ".moving", _protector.ProtectBytes(_protector.UnprotectBytes(File.ReadAllBytes(path), "files"), "files"));
            File.Move(path + ".moving", path, overwrite: true);
            File.SetLastWriteTimeUtc(path, _clock.Now.UtcDateTime);
        }
    }

    private BackupService Service() => new(
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
        NullLogger<BackupService>.Instance);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakePostgres : IPostgresTools
    {
        public string DatabaseName => "hc360_test";

        public Task<long> GetDatabaseSizeAsync(CancellationToken ct) => Task.FromResult(1024L);

        public async Task<IReadOnlyDictionary<string, long>> DumpAsync(string outputFile, bool lowPriority, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(outputFile, [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(8_192)], ct);
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

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
