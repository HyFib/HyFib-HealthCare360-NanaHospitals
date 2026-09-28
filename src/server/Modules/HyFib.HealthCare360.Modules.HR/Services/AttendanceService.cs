using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Daily attendance computation (HR-04): builds each employee-day from the roster (or default shift and weekly off),
/// holidays, approved leave and regularisations, allocates punches to days (night shifts!) and runs the pure
/// <see cref="AttendanceCalculator"/>. Idempotent: re-running for a range recomputes it; payroll-locked days are
/// never changed. Works without a signed-in user (nightly job), so the facility is always explicit.
/// </summary>
public sealed class AttendanceService(HospitalDbContext db, TimeProvider time)
{
    public const int MaxRangeDays = 62;
    private const int EmployeeBatchSize = 100;

    private sealed record EmployeeCalendar(Guid Id, DateOnly JoiningDate, DateOnly? LeavingDate, Guid? DefaultShiftId, Weekday? WeeklyOff);

    /// <summary>Recomputes <paramref name="from"/>..<paramref name="to"/> (local dates) and returns the number of employee-days written.</summary>
    public async Task<int> ComputeAsync(Guid facilityId, DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds, CancellationToken ct)
    {
        if (to < from)
        {
            return 0;
        }

        var employeesQuery = db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(e => e.FacilityId == facilityId && !e.IsAttendanceExempt && e.JoiningDate <= to && (e.LeavingDate == null || e.LeavingDate >= from));
        if (employeeIds is { Count: > 0 })
        {
            employeesQuery = employeesQuery.Where(e => employeeIds.Contains(e.Id));
        }

        var employees = await employeesQuery
            .Select(e => new EmployeeCalendar(e.Id, e.JoiningDate, e.LeavingDate, e.DefaultShiftId, e.WeeklyOff))
            .ToListAsync(ct);
        if (employees.Count == 0)
        {
            return 0;
        }

        var shifts = await db.Set<Shift>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.FacilityId == facilityId)
            .ToDictionaryAsync(s => s.Id, s => s.ToTimes(), ct);
        var holidays = (await db.Set<Holiday>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(h => h.FacilityId == facilityId && h.Date >= from.AddDays(-1) && h.Date <= to.AddDays(1) && h.Kind != HolidayKind.Restricted)
                .Select(h => h.Date)
                .ToListAsync(ct))
            .ToHashSet();
        var leaveTypes = await db.Set<LeaveType>().AsNoTracking().ToDictionaryAsync(t => t.Id, ct);

        var written = 0;
        foreach (var batch in employees.Chunk(EmployeeBatchSize))
        {
            written += await ComputeBatchAsync(facilityId, batch, from, to, shifts, holidays, leaveTypes, ct);
            db.ChangeTracker.Clear();
        }

        return written;
    }

    private async Task<int> ComputeBatchAsync(
        Guid facilityId,
        IReadOnlyList<EmployeeCalendar> batch,
        DateOnly from,
        DateOnly to,
        IReadOnlyDictionary<Guid, ShiftTimes> shifts,
        IReadOnlySet<DateOnly> holidays,
        IReadOnlyDictionary<Guid, LeaveType> leaveTypes,
        CancellationToken ct)
    {
        var ids = batch.Select(e => e.Id).ToList();
        var planFrom = from.AddDays(-1);
        var planTo = to.AddDays(1);

        var roster = (await db.Set<RosterEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(r => ids.Contains(r.EmployeeId) && r.Date >= planFrom && r.Date <= planTo)
                .Select(r => new { r.EmployeeId, r.Date, r.ShiftId, r.IsWeekOff })
                .ToListAsync(ct))
            .ToLookup(r => (r.EmployeeId, r.Date));

        var leaves = (await db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(l => ids.Contains(l.EmployeeId) && l.Status == ApprovalStatus.Approved && l.FromDate <= planTo && l.ToDate >= planFrom)
                .ToListAsync(ct))
            .ToLookup(l => l.EmployeeId);

        var regularisations = (await db.Set<AttendanceRegularisation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(r => ids.Contains(r.EmployeeId) && r.Status == ApprovalStatus.Approved && r.Date >= planFrom && r.Date <= planTo)
                .ToListAsync(ct))
            .ToLookup(r => r.EmployeeId);

        var punchFrom = HrClock.StartOfDayUtc(planFrom);
        var punchTo = HrClock.StartOfDayUtc(planTo.AddDays(1));
        var punches = (await db.Set<AttendancePunch>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(p => p.EmployeeId != null && ids.Contains(p.EmployeeId.Value) && p.PunchedAt >= punchFrom && p.PunchedAt < punchTo)
                .Select(p => new { EmployeeId = p.EmployeeId!.Value, p.PunchedAt })
                .ToListAsync(ct))
            .ToLookup(p => p.EmployeeId, p => HrClock.ToLocal(p.PunchedAt));

        var existing = await db.Set<AttendanceDay>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => ids.Contains(a.EmployeeId) && a.Date >= from && a.Date <= to)
            .ToDictionaryAsync(a => (a.EmployeeId, a.Date), ct);

        var now = time.GetUtcNow();
        var written = 0;
        foreach (var employee in batch)
        {
            var plans = new List<(AttendanceDayPlan Plan, DayKind Kind, Guid? ShiftId)>();
            for (var date = planFrom; date <= planTo; date = date.AddDays(1))
            {
                plans.Add(PlanDay(employee, date, roster[(employee.Id, date)].Select(r => (r.ShiftId, r.IsWeekOff)).ToList(), shifts, holidays));
            }

            var approved = regularisations[employee.Id].ToList();
            var allPunches = punches[employee.Id].Concat(approved.SelectMany(r => r.RequestedPunches()));
            var allocated = PunchAllocator.Allocate(plans.Select(p => p.Plan).ToList(), allPunches);

            foreach (var (plan, kind, shiftId) in plans.Where(p => p.Plan.Date >= from && p.Plan.Date <= to))
            {
                var date = plan.Date;
                if (date < employee.JoiningDate || (employee.LeavingDate is { } left && date > left))
                {
                    continue;
                }

                var (leaveDays, leaveIsPaid) = LeaveOn(leaves[employee.Id], leaveTypes, date, kind != DayKind.Working);
                var dayRegularisations = approved.Where(r => r.Date == date).ToList();
                var dayPunches = allocated.TryGetValue(date, out var list) ? list : [];
                var input = new AttendanceDayInput(
                    date,
                    kind,
                    plan.Shift,
                    dayPunches,
                    leaveDays,
                    leaveIsPaid,
                    IsOnDuty: dayRegularisations.Any(r => r.Kind == RegularisationKind.OnDuty),
                    IsRegularised: dayRegularisations.Count > 0);
                var outcome = AttendanceCalculator.Calculate(input);

                if (!existing.TryGetValue((employee.Id, date), out var day))
                {
                    day = new AttendanceDay { FacilityId = facilityId, EmployeeId = employee.Id, Date = date };
                    db.Add(day);
                }

                if (day.Apply(outcome, shiftId, HrClock.ToUtc, dayPunches.Count, now).IsSuccess)
                {
                    written++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return written;
    }

    /// <summary>
    /// Roster first (a rostered shift wins even on a public holiday — hospitals run 24×7), then rostered week off,
    /// holiday, fixed weekly off, and finally the default shift.
    /// </summary>
    private static (AttendanceDayPlan Plan, DayKind Kind, Guid? ShiftId) PlanDay(
        EmployeeCalendar employee,
        DateOnly date,
        IReadOnlyList<(Guid? ShiftId, bool IsWeekOff)> rosterCells,
        IReadOnlyDictionary<Guid, ShiftTimes> shifts,
        IReadOnlySet<DateOnly> holidays)
    {
        var rostered = rosterCells.Where(c => c.ShiftId is { } id && shifts.ContainsKey(id)).Select(c => c.ShiftId!.Value).ToList();
        if (rostered.Count > 0)
        {
            var combined = ShiftTimes.Combine(rostered.Select(id => shifts[id]).ToList());
            return (new AttendanceDayPlan(date, combined), DayKind.Working, rostered[0]);
        }

        if (rosterCells.Any(c => c.IsWeekOff))
        {
            return (new AttendanceDayPlan(date, null), DayKind.WeekOff, null);
        }

        if (holidays.Contains(date))
        {
            return (new AttendanceDayPlan(date, null), DayKind.Holiday, null);
        }

        if (employee.WeeklyOff is { } weeklyOff && ToWeekday(date) == weeklyOff)
        {
            return (new AttendanceDayPlan(date, null), DayKind.WeekOff, null);
        }

        return employee.DefaultShiftId is { } defaultShift && shifts.TryGetValue(defaultShift, out var times)
            ? (new AttendanceDayPlan(date, times), DayKind.Working, defaultShift)
            : (new AttendanceDayPlan(date, null), DayKind.Working, null);
    }

    /// <summary>Approved leave on a date: its fraction and whether it is paid. Holidays/week offs count only for calendar-day types.</summary>
    private static (decimal Days, bool IsPaid) LeaveOn(IEnumerable<LeaveRequest> leaves, IReadOnlyDictionary<Guid, LeaveType> types, DateOnly date, bool isNonWorkingDay)
    {
        decimal days = 0;
        var paid = true;
        foreach (var leave in leaves)
        {
            var portion = leave.PortionOn(date);
            if (portion <= 0 || !types.TryGetValue(leave.LeaveTypeId, out var type) || (isNonWorkingDay && !type.CountsNonWorkingDays))
            {
                continue;
            }

            days += portion;
            paid &= type.IsPaid;
        }

        return (Math.Min(days, 1), paid);
    }

    internal static Weekday ToWeekday(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? Weekday.Sunday : (Weekday)(int)date.DayOfWeek;
}
