using System.Buffers.Binary;
using HyFib.HealthCare360.Platform.Persistence;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class DatabaseWatchdogTests : IDisposable
{
    private const string Service = "postgresql-x64-17";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 4, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-database-watchdog-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1300u, 1, DatabaseClusterState.ShutDown)]
    [InlineData(1700u, 6, DatabaseClusterState.InProduction)]
    [InlineData(1800u, 2, DatabaseClusterState.ShutDownInRecovery)]
    [InlineData(1800u, 4, DatabaseClusterState.InCrashRecovery)]
    public void The_state_is_read_from_pg_control(uint version, int state, DatabaseClusterState expected) =>
        DatabaseWatchdog.ClusterStateFrom(ControlFile(version, state)).ShouldBe(expected);

    [Fact]
    public void A_file_that_is_not_pg_control_reads_as_unknown()
    {
        DatabaseWatchdog.ClusterStateFrom(ControlFile(1700, 9)).ShouldBe(DatabaseClusterState.Unknown, "no such state");
        DatabaseWatchdog.ClusterStateFrom(ControlFile(42, 1)).ShouldBe(DatabaseClusterState.Unknown, "no such version");
        DatabaseWatchdog.ClusterStateFrom(ControlFile(1700, 1).AsSpan(0, 19)).ShouldBe(DatabaseClusterState.Unknown, "too short");
    }

    [Fact]
    public void The_state_is_read_from_the_data_folder_and_a_missing_one_is_unknown()
    {
        Directory.CreateDirectory(Path.Combine(_root, "global"));

        // pg_control is 8 kB; only its first fields are read.
        var file = new byte[8192];
        ControlFile(1700, 6).CopyTo(file, 0);
        File.WriteAllBytes(Path.Combine(_root, "global", "pg_control"), file);

        DatabaseWatchdog.ReadClusterState(_root).ShouldBe(DatabaseClusterState.InProduction);
        DatabaseWatchdog.ReadClusterState(Path.Combine(_root, "missing")).ShouldBe(DatabaseClusterState.Unknown);
    }

    [Fact]
    public void PostgreSQL_that_crashed_is_started()
    {
        var decision = Decide(DatabaseServiceState.Stopped, DatabaseClusterState.InProduction);

        decision.Start.ShouldBeTrue();
        decision.Reason.ShouldContain("not shut down cleanly");
    }

    [Fact]
    public void PostgreSQL_whose_data_folder_cannot_be_read_is_started_and_says_why()
    {
        var decision = Decide(DatabaseServiceState.Stopped, DatabaseClusterState.Unknown);

        decision.Start.ShouldBeTrue();
        decision.Reason.ShouldContain("pg_control could not be read");
    }

    [Fact]
    public void A_clean_stop_and_a_service_set_to_start_by_hand_are_left_stopped()
    {
        var stopped = Decide(DatabaseServiceState.Stopped, DatabaseClusterState.ShutDown);
        stopped.Start.ShouldBeFalse();
        stopped.Reason.ShouldContain("shut down cleanly, by a person or a script");

        Decide(DatabaseServiceState.Stopped, DatabaseClusterState.ShutDownInRecovery).Start.ShouldBeFalse("a standby shut down cleanly");

        var manual = Decide(DatabaseServiceState.Stopped, DatabaseClusterState.InProduction, startsAutomatically: false);
        manual.Start.ShouldBeFalse();
        manual.Reason.ShouldContain("set to start by hand");
    }

    [Fact]
    public void A_stopped_service_whose_PostgreSQL_still_runs_is_not_started()
    {
        var decision = Decide(DatabaseServiceState.Stopped, DatabaseClusterState.InProduction, runsWithoutService: true);

        decision.Start.ShouldBeFalse();
        decision.Reason.ShouldContain("PostgreSQL itself runs");
    }

    [Fact]
    public void The_postmaster_id_is_read_from_postmaster_pid()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "postmaster.pid"), "6120\nC:/Program Files/PostgreSQL/17/data\n1760243400\n5432\n");

        DatabaseWatchdog.ReadPostmasterId(_root).ShouldBe(6120);

        File.WriteAllText(Path.Combine(_root, "postmaster.pid"), "not a number\n");
        DatabaseWatchdog.ReadPostmasterId(_root).ShouldBeNull();
        DatabaseWatchdog.ReadPostmasterId(Path.Combine(_root, "missing")).ShouldBeNull("shut down cleanly: there is no file");
    }

    [Theory]
    [InlineData(DatabaseServiceState.Running)]
    [InlineData(DatabaseServiceState.Starting)]
    [InlineData(DatabaseServiceState.Stopping)]
    [InlineData(DatabaseServiceState.Paused)]
    [InlineData(DatabaseServiceState.Missing)]
    public void A_service_that_is_not_stopped_is_left_as_it_is(DatabaseServiceState state) =>
        Decide(state, DatabaseClusterState.InProduction).Start.ShouldBeFalse();

    [Fact]
    public void After_three_failed_starts_it_tries_every_quarter_of_an_hour()
    {
        IReadOnlyList<DatabaseRestart> failed =
        [
            new(Now.AddMinutes(-3), false, "it stopped again as it started."),
            new(Now.AddMinutes(-2), false, "it stopped again as it started."),
            new(Now.AddMinutes(-1), false, "it stopped again as it started."),
        ];

        var waiting = Decide(DatabaseServiceState.Stopped, DatabaseClusterState.InCrashRecovery, restarts: failed);
        waiting.Start.ShouldBeFalse();
        waiting.Reason.ShouldContain("the last 3 starts failed: the next try is at 04:44 UTC");

        Decide(DatabaseServiceState.Stopped, DatabaseClusterState.InCrashRecovery, restarts: failed, now: Now.AddMinutes(14)).Start.ShouldBeTrue();
        Decide(DatabaseServiceState.Stopped, DatabaseClusterState.InCrashRecovery, restarts: [.. failed.Take(2)]).Start.ShouldBeTrue("two failures");
        Decide(DatabaseServiceState.Stopped, DatabaseClusterState.InCrashRecovery, restarts: [.. failed, new(Now.AddSeconds(-30), true)])
            .Start.ShouldBeTrue("a start worked since");
    }

    [Fact]
    public void The_record_keeps_the_most_recent_starts()
    {
        IReadOnlyList<DatabaseRestart> restarts = [];
        for (var i = 0; i < DatabaseWatchdog.RestartsKept + 5; i++)
        {
            restarts = DatabaseWatchdog.WithRestart(restarts, new(Now.AddMinutes(i), true));
        }

        restarts.Count.ShouldBe(DatabaseWatchdog.RestartsKept);
        restarts[^1].At.ShouldBe(Now.AddMinutes(DatabaseWatchdog.RestartsKept + 4));
    }

    [Fact]
    public void A_watchdog_with_nothing_to_report_raises_no_warning()
    {
        DatabaseWatchdog.Warnings(null, Now).ShouldBeEmpty("PostgreSQL is on another machine, or the watchdog is not set up");
        DatabaseWatchdog.Warnings(Report([], checkedAt: Now.AddMinutes(-1)), Now).ShouldBeEmpty();
        DatabaseWatchdog.Warnings(Report([new(Now.AddHours(-25), true)]), Now).ShouldBeEmpty("a start more than a day ago");
    }

    [Fact]
    public void Starts_in_the_last_day_are_a_warning()
    {
        var warning = DatabaseWatchdog.Warnings(Report([new(Now.AddHours(-5), true), new(Now.AddHours(-1), true)]), Now).ShouldHaveSingleItem();

        warning.Code.ShouldBe("db.restarted");
        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldStartWith("PostgreSQL stopped without being shut down 2 times in the last 24 hours, and the database watchdog started it again.");
    }

    [Fact]
    public void A_start_that_failed_is_critical_until_PostgreSQL_runs()
    {
        IReadOnlyList<DatabaseRestart> restarts = [new(Now.AddMinutes(-4), false, "it was still starting 2 minutes later.")];

        var failed = DatabaseWatchdog.Warnings(Report(restarts, DatabaseServiceState.Starting), Now).ShouldHaveSingleItem();
        failed.Code.ShouldBe("db.restart_failed");
        failed.Severity.ShouldBe("critical");
        failed.Message.ShouldContain("could not start it again: it was still starting 2 minutes later.");

        DatabaseWatchdog.Warnings(Report(restarts, DatabaseServiceState.Running), Now).ShouldHaveSingleItem().Code.ShouldBe("db.restarted");
    }

    [Fact]
    public void A_watchdog_that_stopped_running_or_lost_its_service_is_reported()
    {
        var stale = DatabaseWatchdog.Warnings(Report([], checkedAt: Now - DatabaseWatchdog.MaxAge - TimeSpan.FromMinutes(1)), Now).ShouldHaveSingleItem();
        stale.Code.ShouldBe("db.watchdog_stale");
        stale.Message.ShouldContain("12 Oct 2026 04:19 UTC");
        stale.Message.ShouldContain(DatabaseWatchdog.ScheduledTaskName);

        var missing = DatabaseWatchdog.Warnings(Report([], DatabaseServiceState.Missing), Now).ShouldHaveSingleItem();
        missing.Code.ShouldBe("db.watchdog_service_missing");
        missing.Message.ShouldContain(Service);
    }

    [Fact]
    public void The_record_survives_a_round_trip_and_a_damaged_one_reads_as_missing()
    {
        var path = Path.Combine(_root, "config", DatabaseWatchdog.FileName);
        var report = Report([new(Now.AddMinutes(-2), false, "it stopped again as it started."), new(Now.AddMinutes(-1), true)]);

        DatabaseWatchdog.Save(path, report);
        var loaded = DatabaseWatchdog.Load(path).ShouldNotBeNull();

        loaded.CheckedAt.ShouldBe(report.CheckedAt);
        loaded.Service.ShouldBe(Service);
        loaded.ServiceState.ShouldBe(DatabaseServiceState.Running);
        loaded.ClusterState.ShouldBe(DatabaseClusterState.InProduction);
        loaded.Restarts.ShouldBe(report.Restarts);
        File.ReadAllText(path).ShouldContain("\"InProduction\"", Case.Sensitive, "enums are written by name");

        File.WriteAllText(path, "{ not json");
        DatabaseWatchdog.Load(path).ShouldBeNull();
        DatabaseWatchdog.Load(Path.Combine(_root, "missing.json")).ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DatabaseWatchdogDecision Decide(
        DatabaseServiceState state, DatabaseClusterState cluster, bool startsAutomatically = true, bool runsWithoutService = false,
        IReadOnlyList<DatabaseRestart>? restarts = null, DateTimeOffset? now = null) =>
        DatabaseWatchdog.Decide(Service, state, startsAutomatically, cluster, runsWithoutService, restarts ?? [], now ?? Now);

    private static DatabaseWatchdogReport Report(
        IReadOnlyList<DatabaseRestart> restarts, DatabaseServiceState state = DatabaseServiceState.Running, DateTimeOffset? checkedAt = null) =>
        new(checkedAt ?? Now, Service, state, DatabaseClusterState.InProduction, restarts);

    /// <summary>The first fields of a pg_control file: system identifier, pg_control version, catalogue version, state.</summary>
    private static byte[] ControlFile(uint version, int state)
    {
        var header = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(header, 7_423_456_789_012_345_678);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 202_406_281);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), state);
        return header;
    }
}
