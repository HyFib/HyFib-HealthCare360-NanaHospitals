namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum AttendanceStatus
{
    Present = 0,
    Absent,
    HalfDay,
    Leave,
    Holiday,
    WeekOff,
    OnDuty,
}

[Flags]
public enum AttendanceFlags
{
    None = 0,
    Late = 1,
    EarlyExit = 2,

    /// <summary>Only one punch: the employee must regularise the missing in/out time.</summary>
    MissingPunch = 4,

    Overtime = 8,

    /// <summary>Worked on a holiday/week off (eligible for comp-off or holiday pay).</summary>
    WorkedOnOffDay = 16,

    /// <summary>No roster entry and no default shift, so late/early could not be judged.</summary>
    Unrostered = 32,

    /// <summary>Times come (partly) from an approved regularisation.</summary>
    Regularised = 64,
}

/// <summary>
/// The computed attendance of one employee on one date. The day fractions (present, paid/unpaid leave, absent,
/// off) always add up to one and are what payroll uses for loss-of-pay, so the display status can stay simple.
/// Recomputed nightly from punches, roster, leave and holidays; locked once the month's payroll is locked.
/// Changes are not data-audited (they are derived); the regularisations that cause manual changes are.
/// </summary>
[NotAudited]
public sealed class AttendanceDay : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public Guid? ShiftId { get; set; }
    public AttendanceStatus Status { get; set; }
    public DateTimeOffset? FirstIn { get; set; }
    public DateTimeOffset? LastOut { get; set; }
    public int WorkedMinutes { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyExitMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public decimal PresentDays { get; set; }
    public decimal PaidLeaveDays { get; set; }
    public decimal UnpaidLeaveDays { get; set; }
    public decimal AbsentDays { get; set; }
    public decimal OffDays { get; set; }
    public AttendanceFlags Flags { get; set; }
    public int PunchCount { get; set; }

    /// <summary>Payroll for the month is locked; automatic recomputation no longer changes this row.</summary>
    public bool IsLocked { get; set; }

    public DateTimeOffset ComputedAt { get; set; }

    /// <summary>Loss-of-pay fraction for this day (absence plus unpaid leave).</summary>
    public decimal LopDays => AbsentDays + UnpaidLeaveDays;

    /// <summary>Copies a calculation result onto this row. Locked rows are left untouched.</summary>
    public Result Apply(AttendanceOutcome outcome, Guid? shiftId, Func<DateTime, DateTimeOffset> toUtc, int punchCount, DateTimeOffset now)
    {
        if (IsLocked)
        {
            return Error.Rule("attendance.locked", "Attendance for this date is locked because payroll has been locked.");
        }

        ShiftId = shiftId;
        Status = outcome.Status;
        FirstIn = outcome.FirstIn is { } firstIn ? toUtc(firstIn) : null;
        LastOut = outcome.LastOut is { } lastOut ? toUtc(lastOut) : null;
        WorkedMinutes = outcome.WorkedMinutes;
        LateMinutes = outcome.LateMinutes;
        EarlyExitMinutes = outcome.EarlyExitMinutes;
        OvertimeMinutes = outcome.OvertimeMinutes;
        PresentDays = outcome.PresentDays;
        PaidLeaveDays = outcome.PaidLeaveDays;
        UnpaidLeaveDays = outcome.UnpaidLeaveDays;
        AbsentDays = outcome.AbsentDays;
        OffDays = outcome.OffDays;
        Flags = outcome.Flags;
        PunchCount = punchCount;
        ComputedAt = now;
        return Result.Success();
    }
}
