namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>Loss-of-pay facts of one computed attendance day.</summary>
public sealed record AttendanceLopDay(DateOnly Date, decimal AbsentDays, decimal UnpaidLeaveDays);

public sealed record LopResult(int DaysInMonth, int EmployedDays, decimal PayableDays, decimal LopDays, int MissingAttendanceDays);

/// <summary>
/// Loss of pay (LOP) for a payroll month on the calendar-day basis common in India (salary × payable days ÷ days in
/// month). Days before joining or after leaving are simply not payable. For employees who punch, a day without any
/// computed attendance counts as absent — and is reported, so payroll can fix missing data before approving.
/// Attendance-exempt staff lose pay only for unpaid leave. A manual override (from payroll inputs) wins.
/// </summary>
public static class LopCalculator
{
    public static LopResult Compute(
        int year,
        int month,
        DateOnly joiningDate,
        DateOnly? leavingDate,
        bool attendanceExempt,
        IReadOnlyList<AttendanceLopDay> attendance,
        decimal? lopDaysOverride = null)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = new DateOnly(year, month, daysInMonth);
        var start = joiningDate > monthStart ? joiningDate : monthStart;
        var end = leavingDate is { } left && left < monthEnd ? left : monthEnd;
        if (end < start)
        {
            return new LopResult(daysInMonth, 0, 0, 0, 0);
        }

        var employedDays = end.DayNumber - start.DayNumber + 1;
        var byDate = attendance
            .Where(a => a.Date >= start && a.Date <= end)
            .GroupBy(a => a.Date)
            .ToDictionary(g => g.Key, g => g.First());

        decimal lop = 0;
        var missing = 0;
        if (lopDaysOverride is { } manual)
        {
            lop = manual;
        }
        else if (attendanceExempt)
        {
            lop = byDate.Values.Sum(a => a.UnpaidLeaveDays);
        }
        else
        {
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                if (byDate.TryGetValue(date, out var day))
                {
                    lop += day.AbsentDays + day.UnpaidLeaveDays;
                }
                else
                {
                    missing++;
                    lop += 1;
                }
            }
        }

        lop = Math.Clamp(lop, 0, employedDays);
        return new LopResult(daysInMonth, employedDays, employedDays - lop, lop, missing);
    }
}
