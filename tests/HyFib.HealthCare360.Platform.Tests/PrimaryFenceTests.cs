using System.Data.Common;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-20: after a failover, the old primary must not take patient data beside the promoted standby. The failover script
/// records the new primary and its timeline on that server and beside the backups' second copy; a server whose database is
/// the same cluster on an older timeline finds it was superseded, and HC360 neither starts nor keeps running on it. The CI
/// Windows job promotes a real standby with the script and starts HC360 on the old primary (Test-StandbyReplication.ps1).
/// </summary>
public sealed class PrimaryFenceTests : IDisposable
{
    private const string Cluster = "7412345678901234567";
    private static readonly DateTimeOffset PromotedAt = new(2026, 11, 14, 4, 32, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;

    public PrimaryFenceTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _options.Backup.SecondaryDirectory = Path.Combine(_root, "nas");
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
        Directory.CreateDirectory(_options.Backup.SecondaryDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_database_on_an_older_timeline_of_the_cluster_a_standby_was_promoted_from_was_superseded()
    {
        var oldPrimary = new DatabaseCluster(Cluster, "000000010000000000000051");

        var record = PrimaryFence.SupersededBy(oldPrimary, [Record(timeline: 2)]).ShouldNotBeNull();

        (record.Primary, record.Timeline).ShouldBe(("HC360-SRV2", 2));
    }

    [Fact]
    public void A_record_of_the_servers_own_timeline_or_an_older_one_or_of_another_cluster_is_no_concern_of_it()
    {
        var promoted = new DatabaseCluster(Cluster, "000000020000000000000060");

        PrimaryFence.SupersededBy(promoted, [Record(timeline: 2)]).ShouldBeNull("the promoted server itself");
        PrimaryFence.SupersededBy(new DatabaseCluster(Cluster, "000000030000000000000070"), [Record(timeline: 2)])
            .ShouldBeNull("promoted again since, or restored to a point in time, which starts a newer timeline");
        PrimaryFence.SupersededBy(new DatabaseCluster("1111111111111111111", "000000010000000000000051"), [Record(timeline: 2)])
            .ShouldBeNull("new hardware restored from a dump is another cluster");
        PrimaryFence.SupersededBy(promoted, []).ShouldBeNull();
    }

    [Fact]
    public void The_newest_timeline_decides_after_failing_over_and_back()
    {
        // SRV1 failed over to SRV2 (timeline 2), was made its standby, and SRV2 failed back over to SRV1 (timeline 3).
        var srv2 = new DatabaseCluster(Cluster, "000000020000000000000080");
        var back = Record(timeline: 3) with { Primary = "HC360-SRV1" };

        PrimaryFence.SupersededBy(srv2, [Record(timeline: 2), back]).ShouldBe(back);
        PrimaryFence.PromotionOf(new DatabaseCluster(Cluster, "000000030000000000000090"), "hc360-srv1", [Record(timeline: 2), back])
            .ShouldBe(back, "a machine's name is compared without regard to case");
        PrimaryFence.PromotionOf(srv2, "HC360-SRV1", [Record(timeline: 2), back]).ShouldBeNull("SRV1 was not promoted to timeline 2");
    }

    [Fact]
    public void The_record_the_failover_script_writes_is_read_from_this_server_and_from_beside_the_backups_second_copy()
    {
        // As Invoke-HC360Failover.ps1 writes it: the system identifier as text (it is a 64-bit number), the time with its offset.
        File.WriteAllText(PrimaryFence.LocalPath(_directories), """
            {
              "format": 1,
              "systemIdentifier": "7412345678901234567",
              "timeline": 2,
              "primary": "HC360-SRV2",
              "promotedAt": "2026-11-14T10:02:00.0000000+05:30",
              "previousPrimary": "10.0.5.11"
            }
            """);
        File.WriteAllText(PrimaryFence.SharedPath(_options)!, """{ "format": 1, "systemIdentifier": "7412345678901234567", "timeline": 3, "primary": "HC360-SRV1", "promotedAt": "2026-11-20T09:00:00Z" }""");

        var records = PrimaryFence.Read(_directories, _options);

        records.Count.ShouldBe(2);
        records[0].ShouldBe(new PrimaryRecord(1, Cluster, 2, "HC360-SRV2", PromotedAt, "10.0.5.11"));
        records[1].Timeline.ShouldBe(3);
    }

    [Fact]
    public void A_record_that_is_missing_unreachable_or_not_one_is_left_out()
    {
        File.WriteAllText(PrimaryFence.LocalPath(_directories), "not json");
        File.WriteAllText(PrimaryFence.SharedPath(_options)!, """{ "format": 2, "systemIdentifier": "7412345678901234567", "timeline": 3, "primary": "HC360-SRV1", "promotedAt": "2026-11-20T09:00:00Z" }""");
        PrimaryFence.Read(_directories, _options).ShouldBeEmpty();

        File.WriteAllText(PrimaryFence.SharedPath(_options)!, """{ "format": 1, "timeline": 3, "primary": "HC360-SRV1", "promotedAt": "2026-11-20T09:00:00Z" }""");
        PrimaryFence.Read(_directories, _options).ShouldBeEmpty("a record without its cluster could fence any server");

        _options.Backup.SecondaryDirectory = Path.Combine(_root, "unplugged-usb-disk");
        PrimaryFence.Read(_directories, _options).ShouldBeEmpty();
        _options.Backup.SecondaryDirectory = null;
        PrimaryFence.SharedPath(_options).ShouldBeNull();
    }

    [Fact]
    public async Task The_start_up_check_runs_without_the_servers_data_folders_and_reads_only_the_shared_record()
    {
        // The database start-up run on its own (DatabaseInitializer given a database context and nothing else, as tests run
        // it) has no data folders (DataDirectories). A record of this server's own is then not read; the shared one is.
        const string Superseding = """{ "format": 1, "systemIdentifier": "7412345678901234567", "timeline": 2, "primary": "HC360-SRV2", "promotedAt": "2026-11-14T04:32:00Z" }""";
        File.WriteAllText(PrimaryFence.LocalPath(_directories), Superseding);
        var nas = _options.Backup.SecondaryDirectory!;
        _options.Backup.SecondaryDirectory = null;
        var ct = TestContext.Current.CancellationToken;

        // Nobody answers at this address: the check succeeds only by not asking, as it does without a record.
        await using var db = new HospitalDbContext(
            new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=hc360_fence_check;Timeout=2").UseSnakeCaseNamingConvention().Options,
            new ModuleCatalog([]));
        PrimaryFence.Read(null, _options).ShouldBeEmpty();
        await PrimaryFence.CheckAsync(db, _options, directories: null, ct);

        // With the data folders, the same record is read and the database asked which cluster and timeline it is on.
        await AskedTheDatabaseAsync(() => PrimaryFence.CheckAsync(db, _options, _directories, ct));

        // The record beside the backups' second copy is read without them.
        _options.Backup.SecondaryDirectory = nas;
        File.WriteAllText(PrimaryFence.SharedPath(_options)!, Superseding.Replace("\"timeline\": 2", "\"timeline\": 3", StringComparison.Ordinal));
        PrimaryFence.Read(null, _options).ShouldHaveSingleItem().Timeline.ShouldBe(3);
        await AskedTheDatabaseAsync(() => PrimaryFence.CheckAsync(db, _options, directories: null, ct));

        // The connection is refused: Npgsql's error, which EF Core reports wrapped as a likely transient failure.
        static async Task AskedTheDatabaseAsync(Func<Task> check)
        {
            var thrown = await Should.ThrowAsync<Exception>(check);
            (thrown as DbException ?? thrown.InnerException as DbException).ShouldNotBeNull(thrown.ToString());
        }
    }

    [Fact]
    public void HC360_says_who_replaced_the_database_and_how_to_make_this_server_its_standby()
    {
        var message = PrimaryFence.SupersededMessage(Record(timeline: 2), new DatabaseCluster(Cluster, "000000010000000000000051"));

        message.ShouldContain("HC360-SRV2");
        message.ShouldContain("14 Nov 2026 04:32 UTC");
        message.ShouldContain("timeline 1");
        message.ShouldContain("New-HC360Standby.ps1 -ReplaceExistingData");
    }

    [Fact]
    public void A_WAL_file_name_gives_the_timeline_it_belongs_to()
    {
        DatabaseCluster.TimelineOf("000000010000000000000051").ShouldBe(1);
        DatabaseCluster.TimelineOf("0000000A0000000000000051").ShouldBe(10);
        Should.Throw<FormatException>(() => DatabaseCluster.TimelineOf("not-a-wal"));
    }

    private static PrimaryRecord Record(int timeline) => new(1, Cluster, timeline, "HC360-SRV2", PromotedAt, "10.0.5.11");
}
