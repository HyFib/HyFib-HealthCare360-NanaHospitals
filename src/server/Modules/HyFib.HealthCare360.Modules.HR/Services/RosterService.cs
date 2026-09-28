using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Rostering;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Shift rostering (HR-03): validates proposed cells with <see cref="RosterConflictDetector"/> against the current
/// roster, approved leave and employment dates, then replaces or appends atomically.
/// </summary>
public sealed class RosterService(HospitalDbContext db)
{
    public async Task<RosterCheckResult> CheckAsync(SaveRosterRequest request, CancellationToken ct)
    {
        var plan = await PlanAsync(request, ct);
        return new RosterCheckResult(
            plan.Conflicts.All(c => c.Severity != ConflictSeverity.Error),
            plan.Conflicts.Any(c => c.Severity == ConflictSeverity.Warning),
            plan.Conflicts);
    }

    public async Task<Result<RosterSaveResult>> SaveAsync(SaveRosterRequest request, CancellationToken ct)
    {
        var plan = await PlanAsync(request, ct);
        if (plan.Conflicts.Any(c => c.Severity == ConflictSeverity.Error))
        {
            return Error.Conflict("roster.conflicts", "The roster has conflicts that must be fixed before saving.", plan.Conflicts);
        }

        var warnings = plan.Conflicts.Where(c => c.Severity == ConflictSeverity.Warning).ToList();
        if (warnings.Count > 0 && !request.AcknowledgeWarnings)
        {
            return Error.Conflict("roster.warnings", "Review the warnings and confirm to save the roster anyway.", warnings);
        }

        foreach (var replaced in plan.Replaced)
        {
            db.Remove(replaced);
        }

        foreach (var cell in plan.NewCells)
        {
            db.Add(new RosterEntry
            {
                FacilityId = plan.FacilityOf[cell.EmployeeId],
                EmployeeId = cell.EmployeeId,
                Date = cell.Date,
                ShiftId = cell.IsWeekOff ? null : cell.ShiftId,
                IsWeekOff = cell.IsWeekOff,
                Notes = string.IsNullOrWhiteSpace(cell.Notes) ? null : cell.Notes.Trim(),
            });
        }

        await db.SaveChangesAsync(ct);
        return new RosterSaveResult(plan.NewCells.Count, plan.Replaced.Count, warnings);
    }

    /// <summary>Copies one week's roster (optionally one department) onto another week, with the usual checks.</summary>
    public async Task<Result<RosterSaveResult>> CopyWeekAsync(CopyRosterWeekRequest request, CancellationToken ct)
    {
        var sourceEnd = request.SourceWeekStart.AddDays(6);
        var shift = request.TargetWeekStart.DayNumber - request.SourceWeekStart.DayNumber;
        var cells = await (
                from r in db.Set<RosterEntry>().AsNoTracking()
                join e in db.Set<Employee>() on r.EmployeeId equals e.Id
                where r.Date >= request.SourceWeekStart && r.Date <= sourceEnd && e.Status != EmployeeStatus.Separated
                      && (request.DepartmentId == null || e.DepartmentId == request.DepartmentId)
                select new { r.EmployeeId, r.Date, r.ShiftId, r.IsWeekOff, r.Notes })
            .ToListAsync(ct);

        if (cells.Count == 0)
        {
            return Error.Rule("roster.empty_week", "Nothing is rostered in the week you are copying from.");
        }

        var assignments = cells.Select(c => new RosterAssignmentInput(c.EmployeeId, c.Date.AddDays(shift), c.ShiftId, c.IsWeekOff, c.Notes)).ToList();
        return await SaveAsync(new SaveRosterRequest(assignments, ReplaceExisting: true, request.AcknowledgeWarnings), ct);
    }

    private sealed record Plan(
        IReadOnlyList<RosterAssignmentInput> NewCells,
        IReadOnlyList<RosterEntry> Replaced,
        IReadOnlyList<RosterConflictDto> Conflicts,
        IReadOnlyDictionary<Guid, Guid> FacilityOf);

    private async Task<Plan> PlanAsync(SaveRosterRequest request, CancellationToken ct)
    {
        var cells = request.Assignments;
        var employeeIds = cells.Select(c => c.EmployeeId).Distinct().ToList();
        var from = cells.Min(c => c.Date).AddDays(-1);
        var to = cells.Max(c => c.Date).AddDays(1);

        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.FacilityId, e.FullName, e.JoiningDate, e.LeavingDate, e.Status })
            .ToListAsync(ct);
        var employment = employees.ToDictionary(e => e.Id, e => new EmploymentWindow(e.JoiningDate, e.LeavingDate, e.Status != EmployeeStatus.Separated));
        var names = employees.ToDictionary(e => e.Id, e => e.FullName);

        var existing = await db.Set<RosterEntry>()
            .Where(r => employeeIds.Contains(r.EmployeeId) && r.Date >= from && r.Date <= to)
            .ToListAsync(ct);

        var shiftIds = cells.Where(c => c.ShiftId is not null).Select(c => c.ShiftId!.Value)
            .Concat(existing.Where(r => r.ShiftId is not null).Select(r => r.ShiftId!.Value))
            .Distinct().ToList();
        var shifts = await db.Set<Shift>().AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        var leave = await db.Set<LeaveRequest>().AsNoTracking()
            .Where(l => employeeIds.Contains(l.EmployeeId) && l.Status == ApprovalStatus.Approved && l.FromDate <= to && l.ToDate >= from)
            .ToListAsync(ct);
        var onLeave = new HashSet<(Guid, DateOnly)>();
        foreach (var request1 in leave)
        {
            for (var date = request1.FromDate; date <= request1.ToDate; date = date.AddDays(1))
            {
                if (request1.PortionOn(date) >= 1)
                {
                    onLeave.Add((request1.EmployeeId, date));
                }
            }
        }

        var conflicts = new List<RosterConflict>();
        foreach (var cell in cells.Where(c => c.ShiftId is { } id && (!shifts.TryGetValue(id, out var s) || !s.IsActive)))
        {
            conflicts.Add(new RosterConflict(cell.EmployeeId, cell.Date, RosterConflictKind.OverlappingShifts, ConflictSeverity.Error, "Choose an active shift."));
        }

        var proposedPairs = cells.Select(c => (c.EmployeeId, c.Date)).ToHashSet();
        var replaced = request.ReplaceExisting ? existing.Where(r => proposedPairs.Contains((r.EmployeeId, r.Date))).ToList() : [];
        var kept = existing.Except(replaced).ToList();

        // Appending an identical cell again is a no-op rather than a duplicate error.
        var newCells = request.ReplaceExisting
            ? cells.ToList()
            : cells.Where(c => !kept.Any(r => r.EmployeeId == c.EmployeeId && r.Date == c.Date && r.ShiftId == c.ShiftId && r.IsWeekOff == c.IsWeekOff)).ToList();

        conflicts.AddRange(RosterConflictDetector.Detect(
            newCells.Select(c => ToAssignment(c.EmployeeId, c.Date, c.ShiftId, c.IsWeekOff, shifts)).ToList(),
            kept.Select(r => ToAssignment(r.EmployeeId, r.Date, r.ShiftId, r.IsWeekOff, shifts)).ToList(),
            employment,
            onLeave));

        var dtos = conflicts
            .Select(c => new RosterConflictDto(c.EmployeeId, names.GetValueOrDefault(c.EmployeeId), c.Date, c.Kind, c.Severity, c.Message))
            .ToList();
        return new Plan(newCells, replaced, dtos, employees.ToDictionary(e => e.Id, e => e.FacilityId));
    }

    private static RosterAssignment ToAssignment(Guid employeeId, DateOnly date, Guid? shiftId, bool isWeekOff, IReadOnlyDictionary<Guid, Shift> shifts) =>
        new(employeeId, date, shiftId, shiftId is { } id && shifts.TryGetValue(id, out var shift) ? shift.ToTimes() : null, isWeekOff);
}
