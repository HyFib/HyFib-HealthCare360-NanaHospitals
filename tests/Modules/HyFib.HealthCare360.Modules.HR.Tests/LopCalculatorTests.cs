using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class LopCalculatorTests
{
    private const int Year = 2026;
    private const int September = 9;
    private static readonly DateOnly LongAgo = new(2020, 1, 1);

    private static List<AttendanceLopDay> Present(DateOnly from, DateOnly to)
    {
        var days = new List<AttendanceLopDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            days.Add(new AttendanceLopDay(d, 0, 0));
        }

        return days;
    }

    private static List<AttendanceLopDay> WholeSeptember() => Present(new DateOnly(Year, 9, 1), new DateOnly(Year, 9, 30));

    [Fact]
    public void Full_attendance_means_no_loss_of_pay()
    {
        var result = LopCalculator.Compute(Year, September, LongAgo, null, false, WholeSeptember());

        result.ShouldBe(new LopResult(30, 30, 30, 0, 0));
    }

    [Fact]
    public void Absences_and_unpaid_leave_are_loss_of_pay()
    {
        var days = WholeSeptember();
        days[3] = days[3] with { AbsentDays = 1 };
        days[4] = days[4] with { AbsentDays = 1 };
        days[10] = days[10] with { UnpaidLeaveDays = 0.5m };

        var result = LopCalculator.Compute(Year, September, LongAgo, null, false, days);

        result.LopDays.ShouldBe(2.5m);
        result.PayableDays.ShouldBe(27.5m);
    }

    [Fact]
    public void Days_without_computed_attendance_are_absent_and_reported()
    {
        var result = LopCalculator.Compute(Year, September, LongAgo, null, false, Present(new DateOnly(Year, 9, 1), new DateOnly(Year, 9, 28)));

        result.MissingAttendanceDays.ShouldBe(2);
        result.LopDays.ShouldBe(2m);
    }

    [Fact]
    public void Joiners_are_paid_from_their_joining_date()
    {
        var joined = new DateOnly(Year, 9, 16);

        var result = LopCalculator.Compute(Year, September, joined, null, false, Present(joined, new DateOnly(Year, 9, 30)));

        result.EmployedDays.ShouldBe(15);
        result.PayableDays.ShouldBe(15m);
        result.LopDays.ShouldBe(0m);
        result.DaysInMonth.ShouldBe(30);
    }

    [Fact]
    public void Leavers_are_paid_up_to_their_last_working_day()
    {
        var left = new DateOnly(Year, 9, 10);

        var result = LopCalculator.Compute(Year, September, LongAgo, left, false, WholeSeptember());

        result.EmployedDays.ShouldBe(10);
        result.PayableDays.ShouldBe(10m);
    }

    [Fact]
    public void Someone_not_employed_in_the_month_has_nothing_payable()
    {
        LopCalculator.Compute(Year, September, new DateOnly(Year, 10, 1), null, false, []).ShouldBe(new LopResult(30, 0, 0, 0, 0));
    }

    [Fact]
    public void Attendance_exempt_staff_lose_pay_only_for_unpaid_leave()
    {
        var days = new List<AttendanceLopDay> { new(new DateOnly(Year, 9, 7), 1, 0), new(new DateOnly(Year, 9, 8), 0, 1) };

        var result = LopCalculator.Compute(Year, September, LongAgo, null, attendanceExempt: true, days);

        result.LopDays.ShouldBe(1m);
        result.MissingAttendanceDays.ShouldBe(0);
    }

    [Fact]
    public void A_manual_override_wins_and_is_capped_at_the_employed_days()
    {
        LopCalculator.Compute(Year, September, LongAgo, null, false, [], lopDaysOverride: 3).LopDays.ShouldBe(3m);
        LopCalculator.Compute(Year, September, new DateOnly(Year, 9, 21), null, false, [], lopDaysOverride: 20).LopDays.ShouldBe(10m);
    }

    [Fact]
    public void Duplicate_rows_for_a_date_are_counted_once()
    {
        var days = WholeSeptember();
        days.Add(new AttendanceLopDay(new DateOnly(Year, 9, 1), 1, 0));

        LopCalculator.Compute(Year, September, LongAgo, null, false, days).LopDays.ShouldBe(0m);
    }

    [Fact]
    public void February_in_a_leap_year_has_29_days()
    {
        LopCalculator.Compute(2028, 2, LongAgo, null, true, []).ShouldBe(new LopResult(29, 29, 29, 0, 0));
    }
}
