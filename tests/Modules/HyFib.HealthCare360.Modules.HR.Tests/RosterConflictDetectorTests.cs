using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class RosterConflictDetectorTests
{
    private static readonly Guid Nurse = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static readonly Guid MorningId = Guid.NewGuid();
    private static readonly Guid EveningId = Guid.NewGuid();
    private static readonly Guid NightId = Guid.NewGuid();
    private static readonly Guid GeneralId = Guid.NewGuid();
    private static readonly ShiftTimes Morning = new(new TimeOnly(7, 0), new TimeOnly(14, 0), 15, 10, 180, 360, 30);
    private static readonly ShiftTimes Evening = new(new TimeOnly(14, 0), new TimeOnly(21, 0), 15, 10, 180, 360, 30);
    private static readonly ShiftTimes Night = new(new TimeOnly(21, 0), new TimeOnly(7, 0), 30, 10, 240, 510, 30);
    private static readonly ShiftTimes General = new(new TimeOnly(9, 0), new TimeOnly(17, 30), 30, 10, 240, 420, 30);

    private static readonly Dictionary<Guid, EmploymentWindow> Employed = new() { [Nurse] = new(new DateOnly(2025, 1, 1), null, true) };

    private static RosterAssignment Cell(int dayOffset, Guid shiftId, ShiftTimes shift) => new(Nurse, Day.AddDays(dayOffset), shiftId, shift, false);

    private static RosterAssignment Off(int dayOffset) => new(Nurse, Day.AddDays(dayOffset), null, null, true);

    private static IReadOnlyList<RosterConflict> Detect(
        IReadOnlyList<RosterAssignment> proposed,
        IReadOnlyList<RosterAssignment>? existing = null,
        IReadOnlySet<(Guid, DateOnly)>? leave = null,
        IReadOnlyDictionary<Guid, EmploymentWindow>? employment = null) =>
        RosterConflictDetector.Detect(proposed, existing ?? [], employment ?? Employed, leave ?? new HashSet<(Guid, DateOnly)>());

    [Fact]
    public void A_normal_rotation_has_no_conflicts()
    {
        Detect([Cell(0, MorningId, Morning), Cell(1, EveningId, Evening), Off(2), Cell(3, NightId, Night)]).ShouldBeEmpty();
    }

    [Fact]
    public void Overlapping_shifts_on_the_same_day_are_an_error()
    {
        var conflict = Detect([Cell(0, GeneralId, General), Cell(0, MorningId, Morning)]).ShouldHaveSingleItem();

        conflict.Kind.ShouldBe(RosterConflictKind.OverlappingShifts);
        conflict.Severity.ShouldBe(ConflictSeverity.Error);
    }

    [Fact]
    public void A_contiguous_double_duty_on_one_day_is_allowed()
    {
        Detect([Cell(0, MorningId, Morning), Cell(0, EveningId, Evening)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_night_shift_that_overlaps_the_next_days_existing_shift_is_an_error()
    {
        var existing = new[] { new RosterAssignment(Nurse, Day.AddDays(1), MorningId, new ShiftTimes(new TimeOnly(6, 0), new TimeOnly(13, 0), 15, 10, 180, 360, 30), false) };

        var conflict = Detect([Cell(0, NightId, Night)], existing).ShouldHaveSingleItem();

        conflict.Kind.ShouldBe(RosterConflictKind.OverlappingShifts);
        conflict.Date.ShouldBe(Day);
    }

    [Fact]
    public void Starting_the_morning_shift_straight_after_a_night_shift_warns_about_rest()
    {
        var conflict = Detect([Cell(0, NightId, Night), Cell(1, MorningId, Morning)]).ShouldHaveSingleItem();

        conflict.Kind.ShouldBe(RosterConflictKind.InsufficientRest);
        conflict.Severity.ShouldBe(ConflictSeverity.Warning);
        conflict.Date.ShouldBe(Day.AddDays(1));
    }

    [Fact]
    public void Two_hours_between_night_and_general_duty_warns_about_rest()
    {
        Detect([Cell(0, NightId, Night), Cell(1, GeneralId, General)]).ShouldHaveSingleItem().Kind.ShouldBe(RosterConflictKind.InsufficientRest);
    }

    [Fact]
    public void Ten_hours_between_evening_and_morning_duty_is_enough_rest()
    {
        Detect([Cell(0, EveningId, Evening), Cell(1, MorningId, Morning)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_shift_and_a_week_off_on_the_same_day_is_an_error()
    {
        Detect([Cell(0, MorningId, Morning), Off(0)]).ShouldContain(c => c.Kind == RosterConflictKind.WeekOffWithShift && c.Severity == ConflictSeverity.Error);
    }

    [Fact]
    public void The_same_shift_twice_on_a_day_is_a_duplicate()
    {
        Detect([Cell(0, MorningId, Morning), Cell(0, MorningId, Morning)]).ShouldHaveSingleItem().Kind.ShouldBe(RosterConflictKind.DuplicateAssignment);
    }

    [Fact]
    public void Rostering_before_joining_or_after_separation_is_an_error()
    {
        var employment = new Dictionary<Guid, EmploymentWindow> { [Nurse] = new(Day.AddDays(1), Day.AddDays(2), true) };

        var conflicts = Detect([Cell(0, MorningId, Morning), Cell(1, MorningId, Morning), Cell(3, MorningId, Morning)], employment: employment);

        conflicts.Where(c => c.Kind == RosterConflictKind.NotEmployed).Select(c => c.Date).ShouldBe([Day, Day.AddDays(3)]);
    }

    [Fact]
    public void Rostering_an_unknown_or_inactive_employee_is_an_error()
    {
        Detect([new RosterAssignment(Guid.NewGuid(), Day, MorningId, Morning, false)]).ShouldHaveSingleItem().Kind.ShouldBe(RosterConflictKind.NotEmployed);

        var inactive = new Dictionary<Guid, EmploymentWindow> { [Nurse] = new(new DateOnly(2025, 1, 1), null, false) };
        Detect([Cell(0, MorningId, Morning)], employment: inactive).ShouldHaveSingleItem().Kind.ShouldBe(RosterConflictKind.NotEmployed);
    }

    [Fact]
    public void Duty_during_approved_leave_is_a_warning()
    {
        var leave = new HashSet<(Guid, DateOnly)> { (Nurse, Day) };

        var conflict = Detect([Cell(0, MorningId, Morning)], leave: leave).ShouldHaveSingleItem();

        conflict.Kind.ShouldBe(RosterConflictKind.OnLeave);
        conflict.Severity.ShouldBe(ConflictSeverity.Warning);
    }

    [Fact]
    public void Problems_between_existing_cells_are_not_reported_for_an_unrelated_change()
    {
        var existing = new[] { Cell(0, GeneralId, General), Cell(0, MorningId, Morning) };

        Detect([Cell(5, EveningId, Evening)], existing).ShouldBeEmpty();
    }
}
