using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class AttendanceCalculatorTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    /// <summary>09:00–17:30 with a 30-minute break: 480 net minutes, 10 minutes grace.</summary>
    private static readonly ShiftTimes General = new(new TimeOnly(9, 0), new TimeOnly(17, 30), 30, 10, 240, 420, 30);

    /// <summary>21:00–07:00 with a 30-minute break: 570 net minutes.</summary>
    private static readonly ShiftTimes Night = new(new TimeOnly(21, 0), new TimeOnly(7, 0), 30, 10, 240, 510, 30);

    private static DateTime At(int hour, int minute, int dayOffset = 0) => Monday.AddDays(dayOffset).ToDateTime(new TimeOnly(hour, minute));

    private static AttendanceOutcome Working(params DateTime[] punches) =>
        AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, General, punches));

    [Fact]
    public void On_time_full_day_is_present_without_late_or_overtime()
    {
        var outcome = Working(At(8, 55), At(17, 35));

        outcome.Status.ShouldBe(AttendanceStatus.Present);
        outcome.FirstIn.ShouldBe(At(8, 55));
        outcome.LastOut.ShouldBe(At(17, 35));
        outcome.WorkedMinutes.ShouldBe(490);
        outcome.LateMinutes.ShouldBe(0);
        outcome.EarlyExitMinutes.ShouldBe(0);
        outcome.OvertimeMinutes.ShouldBe(0);
        outcome.PresentDays.ShouldBe(1m);
        outcome.Flags.ShouldBe(AttendanceFlags.None);
    }

    [Fact]
    public void Arrival_within_grace_is_not_late()
    {
        var outcome = Working(At(9, 8), At(17, 40));

        outcome.LateMinutes.ShouldBe(0);
        outcome.Flags.HasFlag(AttendanceFlags.Late).ShouldBeFalse();
    }

    [Fact]
    public void Arrival_after_grace_is_late_by_the_full_delay_from_shift_start()
    {
        var outcome = Working(At(9, 25), At(17, 40));

        outcome.Status.ShouldBe(AttendanceStatus.Present);
        outcome.LateMinutes.ShouldBe(25);
        outcome.Flags.HasFlag(AttendanceFlags.Late).ShouldBeTrue();
    }

    [Fact]
    public void Leaving_before_shift_end_minus_grace_is_an_early_exit()
    {
        var outcome = Working(At(9, 0), At(16, 30));

        outcome.Status.ShouldBe(AttendanceStatus.Present);
        outcome.WorkedMinutes.ShouldBe(420);
        outcome.EarlyExitMinutes.ShouldBe(60);
        outcome.Flags.HasFlag(AttendanceFlags.EarlyExit).ShouldBeTrue();
    }

    [Fact]
    public void Half_the_shift_is_a_half_day()
    {
        var outcome = Working(At(9, 0), At(13, 30));

        outcome.Status.ShouldBe(AttendanceStatus.HalfDay);
        outcome.WorkedMinutes.ShouldBe(240);
        outcome.PresentDays.ShouldBe(0.5m);
        outcome.AbsentDays.ShouldBe(0.5m);
    }

    [Fact]
    public void Too_little_work_is_absent_and_not_flagged_late()
    {
        var outcome = Working(At(10, 0), At(12, 0));

        outcome.Status.ShouldBe(AttendanceStatus.Absent);
        outcome.AbsentDays.ShouldBe(1m);
        outcome.LateMinutes.ShouldBe(0);
    }

    [Fact]
    public void A_single_punch_is_a_missed_punch_and_counts_as_absent_until_regularised()
    {
        var outcome = Working(At(8, 58));

        outcome.Status.ShouldBe(AttendanceStatus.Absent);
        outcome.FirstIn.ShouldBe(At(8, 58));
        outcome.LastOut.ShouldBeNull();
        outcome.WorkedMinutes.ShouldBe(0);
        outcome.Flags.HasFlag(AttendanceFlags.MissingPunch).ShouldBeTrue();
    }

    [Fact]
    public void No_punches_on_a_working_day_is_absent()
    {
        var outcome = Working();

        outcome.Status.ShouldBe(AttendanceStatus.Absent);
        outcome.FirstIn.ShouldBeNull();
        outcome.AbsentDays.ShouldBe(1m);
    }

    [Fact]
    public void Work_beyond_the_net_shift_is_overtime_once_it_reaches_the_threshold()
    {
        var outcome = Working(At(8, 55), At(19, 30));

        outcome.WorkedMinutes.ShouldBe(605);
        outcome.OvertimeMinutes.ShouldBe(125);
        outcome.Flags.HasFlag(AttendanceFlags.Overtime).ShouldBeTrue();
    }

    [Fact]
    public void Punches_are_ordered_before_first_in_and_last_out_are_taken()
    {
        var outcome = Working(At(17, 35), At(12, 0), At(8, 55));

        outcome.FirstIn.ShouldBe(At(8, 55));
        outcome.LastOut.ShouldBe(At(17, 35));
    }

    [Fact]
    public void Full_day_paid_leave_is_leave_and_payable()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, General, [], LeaveDays: 1));

        outcome.Status.ShouldBe(AttendanceStatus.Leave);
        outcome.PaidLeaveDays.ShouldBe(1m);
        outcome.AbsentDays.ShouldBe(0m);
    }

    [Fact]
    public void Full_day_unpaid_leave_is_leave_without_pay()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, General, [], LeaveDays: 1, LeaveIsPaid: false));

        outcome.Status.ShouldBe(AttendanceStatus.Leave);
        outcome.UnpaidLeaveDays.ShouldBe(1m);
        outcome.PaidLeaveDays.ShouldBe(0m);
    }

    [Fact]
    public void Half_day_leave_plus_half_day_worked_is_a_fully_paid_day()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, General, [At(13, 0), At(17, 35)], LeaveDays: 0.5m));

        outcome.Status.ShouldBe(AttendanceStatus.HalfDay);
        outcome.PresentDays.ShouldBe(0.5m);
        outcome.PaidLeaveDays.ShouldBe(0.5m);
        outcome.AbsentDays.ShouldBe(0m);
        outcome.LateMinutes.ShouldBe(0);
    }

    [Fact]
    public void Half_day_leave_without_work_loses_the_other_half()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, General, [], LeaveDays: 0.5m));

        outcome.Status.ShouldBe(AttendanceStatus.Leave);
        outcome.PaidLeaveDays.ShouldBe(0.5m);
        outcome.AbsentDays.ShouldBe(0.5m);
    }

    [Fact]
    public void Week_off_without_work_is_an_off_day()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.WeekOff, null, []));

        outcome.Status.ShouldBe(AttendanceStatus.WeekOff);
        outcome.OffDays.ShouldBe(1m);
    }

    [Fact]
    public void Work_on_a_holiday_is_present_and_all_of_it_is_overtime()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Holiday, null, [At(9, 0), At(15, 0)]));

        outcome.Status.ShouldBe(AttendanceStatus.Present);
        outcome.WorkedMinutes.ShouldBe(360);
        outcome.OvertimeMinutes.ShouldBe(360);
        outcome.Flags.HasFlag(AttendanceFlags.WorkedOnOffDay).ShouldBeTrue();
        outcome.OffDays.ShouldBe(0m);
    }

    [Fact]
    public void A_short_visit_on_a_holiday_stays_a_holiday()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Holiday, null, [At(10, 0), At(11, 0)]));

        outcome.Status.ShouldBe(AttendanceStatus.Holiday);
        outcome.OffDays.ShouldBe(1m);
    }

    [Fact]
    public void Night_shift_spanning_midnight_is_measured_across_both_dates()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, Night, [At(20, 50), At(7, 5, dayOffset: 1)]));

        outcome.Status.ShouldBe(AttendanceStatus.Present);
        outcome.WorkedMinutes.ShouldBe(585);
        outcome.LateMinutes.ShouldBe(0);
        outcome.EarlyExitMinutes.ShouldBe(0);
        outcome.OvertimeMinutes.ShouldBe(0);
    }

    [Fact]
    public void Approved_on_duty_counts_as_a_worked_day()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, General, [], IsOnDuty: true, IsRegularised: true));

        outcome.Status.ShouldBe(AttendanceStatus.OnDuty);
        outcome.PresentDays.ShouldBe(1m);
        outcome.Flags.HasFlag(AttendanceFlags.Regularised).ShouldBeTrue();
    }

    [Fact]
    public void Unrostered_days_use_the_default_thresholds_and_are_flagged()
    {
        var outcome = AttendanceCalculator.Calculate(new AttendanceDayInput(Monday, DayKind.Working, null, [At(9, 0), At(16, 30)]));

        outcome.Status.ShouldBe(AttendanceStatus.Present);
        outcome.WorkedMinutes.ShouldBe(450);
        outcome.Flags.HasFlag(AttendanceFlags.Unrostered).ShouldBeTrue();
    }

    public static TheoryData<AttendanceDayInput> Scenarios => new()
    {
        new AttendanceDayInput(Monday, DayKind.Working, General, [At(9, 0), At(17, 30)]),
        new AttendanceDayInput(Monday, DayKind.Working, General, [At(9, 0), At(13, 30)]),
        new AttendanceDayInput(Monday, DayKind.Working, General, [At(9, 0)]),
        new AttendanceDayInput(Monday, DayKind.Working, General, [], LeaveDays: 0.5m, LeaveIsPaid: false),
        new AttendanceDayInput(Monday, DayKind.Working, General, [At(13, 0), At(17, 30)], LeaveDays: 0.5m),
        new AttendanceDayInput(Monday, DayKind.Holiday, null, [At(9, 0), At(18, 0)]),
        new AttendanceDayInput(Monday, DayKind.WeekOff, null, []),
        new AttendanceDayInput(Monday, DayKind.Working, General, [], LeaveDays: 1),
        new AttendanceDayInput(Monday, DayKind.Working, General, [], IsOnDuty: true),
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Day_fractions_always_add_up_to_one(AttendanceDayInput input)
    {
        var o = AttendanceCalculator.Calculate(input);

        (o.PresentDays + o.PaidLeaveDays + o.UnpaidLeaveDays + o.AbsentDays + o.OffDays).ShouldBe(1m);
    }
}
