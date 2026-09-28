namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum RosterConflictKind
{
    NotEmployed = 0,
    DuplicateAssignment,
    WeekOffWithShift,
    OverlappingShifts,
    InsufficientRest,
    OnLeave,
}

public enum ConflictSeverity
{
    /// <summary>Allowed after the planner acknowledges it (for example a short turnaround in an emergency).</summary>
    Warning = 0,

    /// <summary>Never saved.</summary>
    Error,
}

/// <summary>A roster cell as seen by the conflict detector (<paramref name="Shift"/> is null for a week off).</summary>
public sealed record RosterAssignment(Guid EmployeeId, DateOnly Date, Guid? ShiftId, ShiftTimes? Shift, bool IsWeekOff);

public sealed record RosterConflict(Guid EmployeeId, DateOnly Date, RosterConflictKind Kind, ConflictSeverity Severity, string Message);

public sealed record EmploymentWindow(DateOnly JoiningDate, DateOnly? LeavingDate, bool IsActive)
{
    public bool Covers(DateOnly date) => IsActive && date >= JoiningDate && (LeavingDate is null || date <= LeavingDate);
}

/// <summary>
/// Validates a proposed roster against the existing one. Errors (overlapping duties, a shift on a rostered week
/// off, rostering someone who is not on the rolls) block saving; warnings (less than the minimum rest between
/// duties, duty during approved leave) must be acknowledged. Only conflicts that involve a proposed cell are
/// reported, so an old problem never blocks an unrelated change.
/// </summary>
public static class RosterConflictDetector
{
    public const int DefaultMinimumRestHours = 8;

    public static IReadOnlyList<RosterConflict> Detect(
        IReadOnlyList<RosterAssignment> proposed,
        IReadOnlyList<RosterAssignment> existing,
        IReadOnlyDictionary<Guid, EmploymentWindow> employment,
        IReadOnlySet<(Guid EmployeeId, DateOnly Date)> approvedLeave,
        int minimumRestHours = DefaultMinimumRestHours)
    {
        var conflicts = new List<RosterConflict>();

        foreach (var cell in proposed)
        {
            if (!employment.TryGetValue(cell.EmployeeId, out var window) || !window.Covers(cell.Date))
            {
                conflicts.Add(new(cell.EmployeeId, cell.Date, RosterConflictKind.NotEmployed, ConflictSeverity.Error,
                    "The employee is not on the rolls on this date."));
            }

            if (cell.Shift is not null && approvedLeave.Contains((cell.EmployeeId, cell.Date)))
            {
                conflicts.Add(new(cell.EmployeeId, cell.Date, RosterConflictKind.OnLeave, ConflictSeverity.Warning,
                    "The employee has approved leave on this date."));
            }
        }

        var all = existing.Select(a => (Cell: a, IsProposed: false))
            .Concat(proposed.Select(a => (Cell: a, IsProposed: true)))
            .ToList();

        foreach (var employee in all.GroupBy(x => x.Cell.EmployeeId))
        {
            DetectDuplicatesAndWeekOffs(employee.ToList(), conflicts);
            DetectOverlapsAndRest(employee.ToList(), minimumRestHours, conflicts);
        }

        return conflicts
            .DistinctBy(c => (c.EmployeeId, c.Date, c.Kind))
            .OrderBy(c => c.Date)
            .ThenByDescending(c => c.Severity)
            .ToList();
    }

    private static void DetectDuplicatesAndWeekOffs(List<(RosterAssignment Cell, bool IsProposed)> cells, List<RosterConflict> conflicts)
    {
        foreach (var day in cells.GroupBy(x => x.Cell.Date))
        {
            var entries = day.ToList();
            if (!entries.Any(e => e.IsProposed))
            {
                continue;
            }

            var employeeId = entries[0].Cell.EmployeeId;
            if (entries.Where(e => e.Cell.ShiftId is not null).GroupBy(e => e.Cell.ShiftId).Any(g => g.Count() > 1)
                || entries.Count(e => e.Cell.IsWeekOff) > 1)
            {
                conflicts.Add(new(employeeId, day.Key, RosterConflictKind.DuplicateAssignment, ConflictSeverity.Error,
                    "The same assignment appears more than once for this date."));
            }

            if (entries.Any(e => e.Cell.IsWeekOff) && entries.Any(e => e.Cell.Shift is not null))
            {
                conflicts.Add(new(employeeId, day.Key, RosterConflictKind.WeekOffWithShift, ConflictSeverity.Error,
                    "A shift and a week off are both rostered on this date."));
            }
        }
    }

    private static void DetectOverlapsAndRest(List<(RosterAssignment Cell, bool IsProposed)> cells, int minimumRestHours, List<RosterConflict> conflicts)
    {
        var duties = cells
            .Where(x => x.Cell.Shift is not null)
            .Select(x => (x.Cell, x.IsProposed, Start: x.Cell.Shift!.StartOn(x.Cell.Date), End: x.Cell.Shift!.EndOn(x.Cell.Date)))
            .OrderBy(d => d.Start)
            .ToList();

        for (var i = 0; i < duties.Count; i++)
        {
            for (var j = i + 1; j < duties.Count; j++)
            {
                var earlier = duties[i];
                var later = duties[j];
                if (!earlier.IsProposed && !later.IsProposed)
                {
                    continue;
                }

                // Same shift on the same date is a duplicate, reported separately.
                if (earlier.Cell.Date == later.Cell.Date && earlier.Cell.ShiftId == later.Cell.ShiftId)
                {
                    continue;
                }

                var reportOn = later.IsProposed ? later.Cell : earlier.Cell;
                if (later.Start < earlier.End)
                {
                    conflicts.Add(new(reportOn.EmployeeId, reportOn.Date, RosterConflictKind.OverlappingShifts, ConflictSeverity.Error,
                        $"Overlaps the duty from {earlier.Start:dd MMM HH:mm} to {earlier.End:dd MMM HH:mm}."));
                    continue;
                }

                var rest = later.Start - earlier.End;
                if (rest < TimeSpan.FromHours(minimumRestHours) && IsBackToBackDuty(earlier.Cell, later.Cell))
                {
                    conflicts.Add(new(reportOn.EmployeeId, reportOn.Date, RosterConflictKind.InsufficientRest, ConflictSeverity.Warning,
                        $"Only {rest.TotalHours:0.#} hours of rest after the previous duty (minimum {minimumRestHours})."));
                }

                // Later duties only start later still; nothing further can overlap or be too close to "earlier".
                if (rest >= TimeSpan.FromHours(minimumRestHours))
                {
                    break;
                }
            }
        }
    }

    /// <summary>Two shifts on the same date form one double duty by design; the rest rule applies between duty days.</summary>
    private static bool IsBackToBackDuty(RosterAssignment earlier, RosterAssignment later) => earlier.Date != later.Date;
}
