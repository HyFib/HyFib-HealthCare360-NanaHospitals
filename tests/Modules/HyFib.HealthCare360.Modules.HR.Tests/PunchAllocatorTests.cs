using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class PunchAllocatorTests
{
    private static readonly DateOnly Day = new(2026, 9, 21);
    private static readonly ShiftTimes General = new(new TimeOnly(9, 0), new TimeOnly(17, 30), 30, 10, 240, 420, 30);
    private static readonly ShiftTimes Night = new(new TimeOnly(21, 0), new TimeOnly(7, 0), 30, 10, 240, 510, 30);

    private static DateTime At(int dayOffset, int hour, int minute) => Day.AddDays(dayOffset).ToDateTime(new TimeOnly(hour, minute));

    [Fact]
    public void Day_shift_punches_stay_on_their_date()
    {
        var allocated = PunchAllocator.Allocate(
            [new(Day, General), new(Day.AddDays(1), General)],
            [At(0, 8, 55), At(0, 17, 40), At(1, 9, 2), At(1, 17, 31)]);

        allocated[Day].ShouldBe([At(0, 8, 55), At(0, 17, 40)]);
        allocated[Day.AddDays(1)].ShouldBe([At(1, 9, 2), At(1, 17, 31)]);
    }

    [Fact]
    public void Night_shift_keeps_its_next_morning_out_punch_and_the_next_shift_gets_its_own()
    {
        var allocated = PunchAllocator.Allocate(
            [new(Day, Night), new(Day.AddDays(1), General)],
            [At(0, 20, 55), At(1, 7, 10), At(1, 8, 52), At(1, 17, 40)]);

        allocated[Day].ShouldBe([At(0, 20, 55), At(1, 7, 10)]);
        allocated[Day.AddDays(1)].ShouldBe([At(1, 8, 52), At(1, 17, 40)]);
    }

    [Fact]
    public void Night_shift_before_a_week_off_keeps_its_out_punch()
    {
        var allocated = PunchAllocator.Allocate(
            [new(Day, Night), new(Day.AddDays(1), null)],
            [At(0, 21, 3), At(1, 7, 20), At(1, 15, 0)]);

        allocated[Day].ShouldBe([At(0, 21, 3), At(1, 7, 20)]);
        allocated[Day.AddDays(1)].ShouldBe([At(1, 15, 0)]);
    }

    [Fact]
    public void Days_without_a_shift_own_their_calendar_day()
    {
        var allocated = PunchAllocator.Allocate(
            [new(Day, null), new(Day.AddDays(1), null)],
            [At(0, 0, 5), At(0, 23, 55), At(1, 0, 0)]);

        allocated[Day].ShouldBe([At(0, 0, 5), At(0, 23, 55)]);
        allocated[Day.AddDays(1)].ShouldBe([At(1, 0, 0)]);
    }

    [Fact]
    public void Punches_outside_every_window_are_ignored()
    {
        var allocated = PunchAllocator.Allocate([new(Day, General)], [At(-3, 9, 0), At(0, 9, 0), At(3, 9, 0)]);

        allocated[Day].ShouldBe([At(0, 9, 0)]);
    }

    [Fact]
    public void Windows_never_overlap_so_a_punch_is_counted_once()
    {
        var windows = PunchAllocator.BuildWindows(
        [
            new(Day, Night), new(Day.AddDays(1), General), new(Day.AddDays(2), null), new(Day.AddDays(3), Night), new(Day.AddDays(4), Night),
        ]);

        for (var i = 0; i + 1 < windows.Count; i++)
        {
            windows[i].To.ShouldBeLessThanOrEqualTo(windows[i + 1].From);
            windows[i].From.ShouldBeLessThan(windows[i].To);
        }
    }
}
