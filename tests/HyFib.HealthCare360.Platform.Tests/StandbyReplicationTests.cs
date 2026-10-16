using HyFib.HealthCare360.Platform.Persistence;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-20, REL-02: System health on the primary says when the warm standby is away or behind, when it can no longer
/// catch up, and when WAL archiving fails. Nothing shows while neither is set up. The integration tests read the same
/// state from a real PostgreSQL with an account like the server's (StandbyReplicationTests there).
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

    private static ReplicationState State(StandbySlot slot) => new([slot], null);
}
