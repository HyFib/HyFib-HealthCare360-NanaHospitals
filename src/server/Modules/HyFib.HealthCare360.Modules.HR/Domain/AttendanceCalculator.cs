namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum DayKind
{
    Working = 0,
    WeekOff,
    Holiday,
}

/// <summary>Worked-minute thresholds used when no shift is rostered (unrostered day or work on a day off).</summary>
public sealed record AttendanceThresholds(int HalfDayMinutes, int FullDayMinutes, int OvertimeThresholdMinutes)
{
    /// <summary>4 hours for a half day and 7 hours for a full day, as in most Indian hospital HR policies.</summary>
    public static AttendanceThresholds Default { get; } = new(240, 420, 30);
}

/// <summary>Everything known about one employee-day. Punches are local (IST) wall-clock times already allocated to this day.</summary>
public sealed record AttendanceDayInput(
    DateOnly Date,
    DayKind DayKind,
    ShiftTimes? Shift,
    IReadOnlyList<DateTime> Punches,
    decimal LeaveDays = 0,
    bool LeaveIsPaid = true,
    bool IsOnDuty = false,
    bool IsRegularised = false);

/// <summary>
/// Result for one day. <see cref="PresentDays"/> + <see cref="PaidLeaveDays"/> + <see cref="UnpaidLeaveDays"/> +
/// <see cref="AbsentDays"/> + <see cref="OffDays"/> is always exactly 1.
/// </summary>
public sealed record AttendanceOutcome(
    AttendanceStatus Status,
    DateTime? FirstIn,
    DateTime? LastOut,
    int WorkedMinutes,
    int LateMinutes,
    int EarlyExitMinutes,
    int OvertimeMinutes,
    decimal PresentDays,
    decimal PaidLeaveDays,
    decimal UnpaidLeaveDays,
    decimal AbsentDays,
    decimal OffDays,
    AttendanceFlags Flags);

/// <summary>
/// Pure daily attendance rules (HR-04):
/// <list type="bullet">
/// <item>approved on-duty counts as a worked day; a full-day approved leave is <see cref="AttendanceStatus.Leave"/>;</item>
/// <item>first punch = in, last punch = out; worked = span − shift break; a single punch is a missed punch
/// (absent until regularised, so punching in and walking out is never paid);</item>
/// <item>worked ≥ full-day minutes → present, ≥ half-day minutes → half day, otherwise absent;</item>
/// <item>late = minutes after shift start once the grace period is exceeded; early exit likewise before shift end;</item>
/// <item>overtime = worked beyond the shift's net minutes once it reaches the overtime threshold; all work on a
/// holiday/week off is overtime (comp-off eligible).</item>
/// </list>
/// </summary>
public static class AttendanceCalculator
{
    public static AttendanceOutcome Calculate(AttendanceDayInput input, AttendanceThresholds? fallback = null)
    {
        var thresholds = input.Shift is { } s
            ? new AttendanceThresholds(s.HalfDayMinutes, s.FullDayMinutes, s.OvertimeThresholdMinutes)
            : fallback ?? AttendanceThresholds.Default;

        var punches = input.Punches.Order().ToList();
        DateTime? firstIn = punches.Count > 0 ? punches[0] : null;
        DateTime? lastOut = punches.Count > 1 ? punches[^1] : null;
        var flags = input.IsRegularised ? AttendanceFlags.Regularised : AttendanceFlags.None;
        if (punches.Count == 1)
        {
            flags |= AttendanceFlags.MissingPunch;
        }

        var worked = firstIn is { } i && lastOut is { } o
            ? Math.Max(0, (int)(o - i).TotalMinutes - (input.Shift?.BreakMinutes ?? 0))
            : 0;

        if (input.IsOnDuty)
        {
            return Outcome(AttendanceStatus.OnDuty, firstIn, lastOut, worked, flags, present: 1);
        }

        var leave = Math.Clamp(input.LeaveDays, 0m, 1m);
        if (leave >= 1)
        {
            return input.LeaveIsPaid
                ? Outcome(AttendanceStatus.Leave, firstIn, lastOut, worked, flags, paidLeave: 1)
                : Outcome(AttendanceStatus.Leave, firstIn, lastOut, worked, flags, unpaidLeave: 1);
        }

        if (input.DayKind != DayKind.Working)
        {
            if (worked >= thresholds.HalfDayMinutes)
            {
                var overtime = worked >= thresholds.OvertimeThresholdMinutes ? worked : 0;
                flags |= AttendanceFlags.WorkedOnOffDay | (overtime > 0 ? AttendanceFlags.Overtime : AttendanceFlags.None);
                return Outcome(AttendanceStatus.Present, firstIn, lastOut, worked, flags, present: 1, overtime: overtime);
            }

            var offStatus = input.DayKind == DayKind.Holiday ? AttendanceStatus.Holiday : AttendanceStatus.WeekOff;
            return Outcome(offStatus, firstIn, lastOut, worked, flags, off: 1);
        }

        if (input.Shift is null)
        {
            flags |= AttendanceFlags.Unrostered;
        }

        var halfLeave = leave > 0;
        var paidLeave = halfLeave && input.LeaveIsPaid ? leave : 0;
        var unpaidLeave = halfLeave && !input.LeaveIsPaid ? leave : 0;

        AttendanceStatus status;
        decimal present;
        if (halfLeave)
        {
            // Half-day leave: the other half is worked (≥ half-day minutes) or lost.
            (status, present) = worked >= thresholds.HalfDayMinutes ? (AttendanceStatus.HalfDay, 1 - leave) : (AttendanceStatus.Leave, 0m);
        }
        else
        {
            (status, present) = worked >= thresholds.FullDayMinutes ? (AttendanceStatus.Present, 1m)
                : worked >= thresholds.HalfDayMinutes ? (AttendanceStatus.HalfDay, 0.5m)
                : (AttendanceStatus.Absent, 0m);
        }

        var late = 0;
        var early = 0;
        var overtimeMinutes = 0;
        if (input.Shift is { } shift && present > 0)
        {
            if (!halfLeave && firstIn is { } arrival)
            {
                var start = shift.StartOn(input.Date);
                if (arrival > start.AddMinutes(shift.GraceMinutes))
                {
                    late = (int)(arrival - start).TotalMinutes;
                    flags |= AttendanceFlags.Late;
                }
            }

            if (!halfLeave && lastOut is { } departure)
            {
                var end = shift.EndOn(input.Date);
                if (departure < end.AddMinutes(-shift.GraceMinutes))
                {
                    early = (int)(end - departure).TotalMinutes;
                    flags |= AttendanceFlags.EarlyExit;
                }
            }
        }

        if (present > 0)
        {
            var scheduled = input.Shift?.NetMinutes ?? thresholds.FullDayMinutes;
            var extra = worked - scheduled;
            if (extra >= thresholds.OvertimeThresholdMinutes && extra > 0)
            {
                overtimeMinutes = extra;
                flags |= AttendanceFlags.Overtime;
            }
        }

        var absent = 1 - present - paidLeave - unpaidLeave;
        return new AttendanceOutcome(status, firstIn, lastOut, worked, late, early, overtimeMinutes, present, paidLeave, unpaidLeave, absent, 0, flags);
    }

    private static AttendanceOutcome Outcome(
        AttendanceStatus status,
        DateTime? firstIn,
        DateTime? lastOut,
        int worked,
        AttendanceFlags flags,
        decimal present = 0,
        decimal paidLeave = 0,
        decimal unpaidLeave = 0,
        decimal off = 0,
        int overtime = 0) =>
        new(status, firstIn, lastOut, worked, 0, 0, overtime, present, paidLeave, unpaidLeave, 0, off, flags);
}
