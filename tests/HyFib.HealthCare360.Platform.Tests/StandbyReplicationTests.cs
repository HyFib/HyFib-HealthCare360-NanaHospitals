using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-20, REL-02: System health on the primary says when the warm standby is away or behind, in bytes and in time
/// (critically once a failover would lose a quarter of an hour), when it can no longer catch up, and when WAL archiving
/// fails. Nothing shows while neither is set up. The integration tests read the same state from a real PostgreSQL with an
/// account like the server's (StandbyReplicationTests there).
/// </summary>
public sealed class StandbyReplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_shows_without_a_standby_or_archiving()
    {
        StandbyReplication.Warnings(new ReplicationState([], null)).ShouldBeEmpty();
    }

    [Fact]
    public void A_standby_that_keeps_up_raises_nothing()
    {
        StandbyReplication.Warnings(State(new StandbySlot("hc360_standby", true, "reserved", 4096))).ShouldBeEmpty();
    }

    [Fact]
    public void A_standby_that_is_away_is_a_warning_naming_its_slot_and_how_far_behind_it_is()
    {
        var warning = StandbyReplication.Warnings(State(new StandbySlot("hc360_standby", false, "extended", 50L * 1024 * 1024))).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("replication.standby_disconnected", "warning"));
        warning.Message.ShouldContain("hc360_standby");
        warning.Message.ShouldContain("50 MB behind");
    }

    [Fact]
    public void A_connected_standby_far_behind_is_a_warning()
    {
        var warning = StandbyReplication.Warnings(State(new StandbySlot("hc360_standby", true, "reserved", 3L << 30))).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("replication.standby_behind", "warning"));
        warning.Message.ShouldContain("3072 MB behind");
        StandbyReplication.Warnings(State(new StandbySlot("hc360_standby", true, "reserved", StandbyReplication.BehindWarningBytes))).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("unreserved", "replication.standby_unreserved", "about to remove")]
    [InlineData("lost", "replication.standby_lost", "New-HC360Standby.ps1")]
    public void A_standby_whose_WAL_is_going_or_gone_is_critical(string walStatus, string code, string says)
    {
        var warning = StandbyReplication.Warnings(State(new StandbySlot("hc360_standby", false, walStatus, null))).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe((code, "critical"));
        warning.Message.ShouldContain(says);
    }

    [Fact]
    public void Archiving_that_fails_is_critical_until_a_file_is_archived_again()
    {
        var failing = new WalArchiverState(120, Now.AddHours(-2), 3, "000000010000000000000079", Now.AddMinutes(-1));
        var warning = StandbyReplication.Warnings(new ReplicationState([], failing)).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("replication.archive_failing", "critical"));
        warning.Message.ShouldContain("000000010000000000000079");

        StandbyReplication.Warnings(new ReplicationState([], failing with { LastArchivedAt = Now })).ShouldBeEmpty();
        StandbyReplication.Warnings(new ReplicationState([], failing with { ArchivedCount = 0, LastArchivedAt = null })).ShouldHaveSingleItem();
        StandbyReplication.Warnings(new ReplicationState([], new WalArchiverState(120, Now, 0, null, null))).ShouldBeEmpty();
    }

    [Fact]
    public void A_standby_away_for_a_quarter_of_an_hour_is_critical_and_says_since_when()
    {
        var away = new StandbySlot("hc360_standby", false, "reserved", 50L * 1024 * 1024, InactiveSince: Now.AddMinutes(-16));

        var warning = StandbyReplication.Warnings(State(away), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("replication.standby_disconnected", "critical"));
        warning.Message.ShouldContain("since 09:44", Case.Sensitive, "04:14 UTC, in the hospital's time");
        StandbyReplication.Warnings(State(away with { InactiveSince = Now.AddMinutes(-14) }), Now).ShouldHaveSingleItem().Severity.ShouldBe("warning");
        StandbyReplication.Warnings(State(away with { InactiveSince = null }), Now).ShouldHaveSingleItem().Severity
            .ShouldBe("warning", "PostgreSQL before 17 does not record since when");
    }

    [Fact]
    public void A_standby_that_applies_changes_within_a_minute_raises_no_lag_warning()
    {
        SystemHealthStandby.Warnings(Streaming(TimeSpan.FromSeconds(3), 4096)).ShouldBeEmpty();

        // An idle primary: once the standby has applied everything, PostgreSQL measures no lag at all.
        SystemHealthStandby.Warnings(Streaming(null, 0)).ShouldBeEmpty();
    }

    [Fact]
    public void A_standby_a_minute_behind_is_a_warning_and_a_quarter_of_an_hour_behind_is_critical()
    {
        var behind = SystemHealthStandby.Warnings(Streaming(TimeSpan.FromMinutes(3), 80L << 20)).ShouldHaveSingleItem();
        (behind.Code, behind.Severity).ShouldBe(("replication.standby_lagging", "warning"));
        behind.Message.ShouldContain("3 minutes behind");

        var far = SystemHealthStandby.Warnings(Streaming(TimeSpan.FromMinutes(15), 900L << 20)).ShouldHaveSingleItem();
        (far.Code, far.Severity).ShouldBe(("replication.standby_lagging", "critical"));
        SystemHealthStandby.Warnings(Streaming(TimeSpan.FromHours(3), 900L << 20)).ShouldHaveSingleItem().Message.ShouldContain("3 hours behind");
    }

    [Fact]
    public void Lag_that_cannot_be_measured_says_how_to_let_HC360_measure_it()
    {
        var slot = new StandbySlot("hc360_standby", true, "reserved", 4096);

        var missing = SystemHealthStandby.Warnings(new ReplicationState([slot], null, Streams: null)).ShouldHaveSingleItem();
        (missing.Code, missing.Severity).ShouldBe(("replication.lag_unknown", "info"));
        missing.Message.ShouldContain("Enable-HC360Replication.ps1");

        // Restored from a dump, the function belongs to the database's owner, which may not read the lag either.
        var restored = new StandbyStream("hc360_standby", null, null, null, null, null);
        SystemHealthStandby.Warnings(new ReplicationState([slot], null, [restored])).ShouldHaveSingleItem().Code.ShouldBe("replication.lag_unknown");
    }

    [Fact]
    public void A_standby_that_is_away_raises_no_lag_warning_of_its_own()
    {
        var away = new StandbySlot("hc360_standby", false, "reserved", 4096, InactiveSince: Now.AddHours(-1));

        SystemHealthStandby.Warnings(new ReplicationState([away], null, [])).ShouldBeEmpty("its slot's warning says it is away");
    }

    private static ReplicationState State(StandbySlot slot) => new([slot], null);

    private static ReplicationState Streaming(TimeSpan? replayLag, long behindBytes) =>
        new([new StandbySlot("hc360_standby", true, "reserved", behindBytes)], null,
            [new StandbyStream("hc360_standby", "streaming", replayLag, replayLag, behindBytes, Now)]);
}
