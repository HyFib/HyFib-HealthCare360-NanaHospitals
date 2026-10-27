using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

/// <summary>A downtime is ongoing until it ends, waits for its back-entry, and closes when that is signed off; times must make sense.</summary>
public sealed class DowntimeEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_downtime_ends_and_closes_with_its_back_entry()
    {
        var downtime = new DowntimeEvent { StartedAt = Now.AddHours(-3) };
        (downtime.Status, downtime.Minutes(Now)).ShouldBe((DowntimeStatus.Ongoing, 180));

        downtime.End(Now.AddHours(-1), "  Replaced the switch.  ", "Meena", Now).IsSuccess.ShouldBeTrue();
        (downtime.Status, downtime.Minutes(Now), downtime.ActionsTaken).ShouldBe((DowntimeStatus.BackEntryPending, 120, "Replaced the switch."));

        downtime.SignOffBackEntry(42, " ", "Ravi", Now).IsSuccess.ShouldBeTrue();
        (downtime.Status, downtime.PaperRecords, downtime.BackEntryNote, downtime.BackEntrySignedByName)
            .ShouldBe((DowntimeStatus.Closed, 42, (string?)null, "Ravi"));
    }

    [Fact]
    public void It_cannot_end_twice_be_signed_off_while_ongoing_or_be_signed_off_twice()
    {
        var downtime = new DowntimeEvent { StartedAt = Now.AddHours(-2) };
        downtime.SignOffBackEntry(1, null, null, Now).Error!.Code.ShouldBe("downtime.ongoing");

        downtime.End(Now.AddHours(-1), null, null, Now).IsSuccess.ShouldBeTrue();
        downtime.End(Now, null, null, Now).Error!.Code.ShouldBe("downtime.ended");

        downtime.SignOffBackEntry(-1, null, null, Now).IsFailure.ShouldBeTrue();
        downtime.SignOffBackEntry(0, null, null, Now).IsSuccess.ShouldBeTrue();
        downtime.SignOffBackEntry(0, null, null, Now).Error!.Code.ShouldBe("downtime.closed");
    }

    [Theory]
    [InlineData(-60, null, true)]
    [InlineData(-60, -30, true)]
    [InlineData(30, null, false)]
    [InlineData(-60, 30, false)]
    [InlineData(-60, -60, false)]
    [InlineData(-60, -90, false)]
    public void Times_are_not_in_the_future_and_the_end_follows_the_start(int startMinutes, int? endMinutes, bool valid) =>
        DowntimeEvent.CheckTimes(Now.AddMinutes(startMinutes), endMinutes is { } end ? Now.AddMinutes(end) : null, Now).IsSuccess.ShouldBe(valid);
}
