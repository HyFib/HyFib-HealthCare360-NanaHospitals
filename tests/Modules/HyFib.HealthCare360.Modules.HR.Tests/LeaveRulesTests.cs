using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class LeaveRulesTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);
    private static readonly DateOnly Sunday = Monday.AddDays(6);

    private static bool NoHolidays(DateOnly _) => false;

    private static decimal Count(DateOnly from, LeaveSession fromSession, DateOnly to, LeaveSession toSession, Func<DateOnly, bool>? nonWorking = null, bool calendarDays = false) =>
        LeaveDayCounter.Count(from, fromSession, to, toSession, nonWorking ?? NoHolidays, calendarDays);

    [Fact]
    public void Counts_full_working_days()
    {
        Count(Monday, LeaveSession.FirstHalf, Monday.AddDays(4), LeaveSession.SecondHalf).ShouldBe(5m);
    }

    [Fact]
    public void Skips_week_offs_and_holidays()
    {
        var wednesday = Monday.AddDays(2);
        bool NonWorking(DateOnly d) => d == Sunday || d == wednesday;

        Count(Monday, LeaveSession.FirstHalf, Sunday, LeaveSession.SecondHalf, NonWorking).ShouldBe(5m);
    }

    [Fact]
    public void Calendar_day_types_count_holidays_and_week_offs()
    {
        Count(Monday, LeaveSession.FirstHalf, Sunday, LeaveSession.SecondHalf, d => d == Sunday, calendarDays: true).ShouldBe(7m);
    }

    [Theory]
    [InlineData(LeaveSession.FirstHalf, LeaveSession.FirstHalf, 0.5)]
    [InlineData(LeaveSession.SecondHalf, LeaveSession.SecondHalf, 0.5)]
    [InlineData(LeaveSession.FirstHalf, LeaveSession.SecondHalf, 1)]
    [InlineData(LeaveSession.SecondHalf, LeaveSession.FirstHalf, 0)]
    public void Single_day_sessions(LeaveSession from, LeaveSession to, double expected)
    {
        Count(Monday, from, Monday, to).ShouldBe((decimal)expected);
    }

    [Fact]
    public void Multi_day_leave_can_start_and_end_with_half_days()
    {
        Count(Monday, LeaveSession.SecondHalf, Monday.AddDays(2), LeaveSession.FirstHalf).ShouldBe(2m);
    }

    [Fact]
    public void A_half_day_on_a_non_working_boundary_is_not_counted_twice()
    {
        Count(Sunday.AddDays(-1), LeaveSession.SecondHalf, Sunday, LeaveSession.SecondHalf, d => d == Sunday).ShouldBe(0.5m);
    }

    [Fact]
    public void Impossible_ranges_count_zero()
    {
        Count(Monday, LeaveSession.FirstHalf, Monday.AddDays(-1), LeaveSession.SecondHalf).ShouldBe(0m);
        Count(Monday, LeaveSession.FirstHalf, Monday.AddDays(LeaveDayCounter.MaxSpanDays), LeaveSession.SecondHalf).ShouldBe(0m);
    }

    [Theory]
    [InlineData(LeaveSession.FirstHalf, LeaveSession.SecondHalf, false)]
    [InlineData(LeaveSession.SecondHalf, LeaveSession.SecondHalf, true)]
    [InlineData(LeaveSession.FirstHalf, LeaveSession.FirstHalf, true)]
    public void Detects_half_day_applications(LeaveSession from, LeaveSession to, bool expected) =>
        LeaveDayCounter.IncludesHalfDay(from, to).ShouldBe(expected);

    [Theory]
    [InlineData("2025-06-01", 12, 12)]
    [InlineData("2026-03-10", 12, 10)]
    [InlineData("2026-03-20", 12, 9)]
    [InlineData("2026-07-20", 15, 6)]
    [InlineData("2026-12-31", 12, 0)]
    [InlineData("2027-01-01", 12, 0)]
    public void New_joiners_get_a_pro_rated_quota_rounded_down_to_half_days(string joined, double quota, double expected)
    {
        LeaveEntitlement.ProratedCredit((decimal)quota, DateOnly.Parse(joined, System.Globalization.CultureInfo.InvariantCulture), 2026).ShouldBe((decimal)expected);
    }

    [Fact]
    public void Pro_rating_keeps_half_days()
    {
        LeaveEntitlement.ProratedCredit(15, new DateOnly(2026, 6, 1), 2026).ShouldBe(8.5m);
    }

    [Theory]
    [InlineData(20, 45, 20)]
    [InlineData(60, 45, 45)]
    [InlineData(-3, 45, 0)]
    [InlineData(10, 0, 0)]
    public void Carry_forward_is_capped_and_never_negative(double closing, double cap, double expected) =>
        LeaveEntitlement.CarryForward((decimal)closing, (decimal)cap).ShouldBe((decimal)expected);

    [Fact]
    public void Pending_requests_reserve_their_days()
    {
        LeaveEntitlement.Available(closingBalance: 10, pendingDays: 3.5m).ShouldBe(6.5m);
    }

    [Theory]
    [InlineData(6.75, 6.5)]
    [InlineData(6.4, 6)]
    [InlineData(7, 7)]
    public void Rounds_down_to_half_days(double value, double expected) =>
        LeaveEntitlement.RoundDownToHalf((decimal)value).ShouldBe((decimal)expected);
}
