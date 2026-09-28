namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>Counts leave days for an application (HR-05).</summary>
public static class LeaveDayCounter
{
    /// <summary>Applications are limited to one year so that counting stays bounded.</summary>
    public const int MaxSpanDays = 366;

    /// <summary>
    /// Days between <paramref name="from"/> and <paramref name="to"/> inclusive. Starting in the second half or ending
    /// in the first half removes half a day at that end. Holidays and week offs are skipped unless
    /// <paramref name="countNonWorkingDays"/> (maternity leave). Returns 0 for an impossible range such as
    /// "second half to first half of the same day".
    /// </summary>
    public static decimal Count(
        DateOnly from,
        LeaveSession fromSession,
        DateOnly to,
        LeaveSession toSession,
        Func<DateOnly, bool> isNonWorkingDay,
        bool countNonWorkingDays)
    {
        if (to < from || to.DayNumber - from.DayNumber >= MaxSpanDays)
        {
            return 0;
        }

        if (from == to && fromSession == LeaveSession.SecondHalf && toSession == LeaveSession.FirstHalf)
        {
            return 0;
        }

        var days = 0m;
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (!countNonWorkingDays && isNonWorkingDay(date))
            {
                continue;
            }

            var value = 1m;
            if (date == from && fromSession == LeaveSession.SecondHalf)
            {
                value -= 0.5m;
            }

            if (date == to && toSession == LeaveSession.FirstHalf)
            {
                value -= 0.5m;
            }

            days += value;
        }

        return days;
    }

    /// <summary>True when the application starts or ends with a half day (types without half days reject this).</summary>
    public static bool IncludesHalfDay(LeaveSession fromSession, LeaveSession toSession) =>
        fromSession == LeaveSession.SecondHalf || toSession == LeaveSession.FirstHalf;
}

/// <summary>Yearly entitlement rules.</summary>
public static class LeaveEntitlement
{
    /// <summary>
    /// Annual quota for <paramref name="year"/>. Joiners get the months remaining in the year (the joining month
    /// counts when they joined by the 15th), rounded down to the nearest half day so nobody is over-credited.
    /// </summary>
    public static decimal ProratedCredit(decimal annualQuota, DateOnly joiningDate, int year)
    {
        if (annualQuota <= 0 || joiningDate.Year > year)
        {
            return 0;
        }

        if (joiningDate.Year < year)
        {
            return annualQuota;
        }

        var months = 12 - joiningDate.Month + (joiningDate.Day <= 15 ? 1 : 0);
        return RoundDownToHalf(annualQuota * months / 12m);
    }

    /// <summary>Unused days carried into the new year, never negative and never above the policy cap.</summary>
    public static decimal CarryForward(decimal previousClosing, decimal cap) =>
        Math.Min(Math.Max(previousClosing, 0), Math.Max(cap, 0));

    /// <summary>Days that can still be applied: the closing balance minus days reserved by pending requests.</summary>
    public static decimal Available(decimal closingBalance, decimal pendingDays) => closingBalance - pendingDays;

    public static decimal RoundDownToHalf(decimal value) => Math.Floor(value * 2) / 2;
}
