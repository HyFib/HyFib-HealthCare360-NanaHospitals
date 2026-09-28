using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.Rostering;

public sealed record ShiftDto(
    Guid Id,
    string Code,
    string Name,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int BreakMinutes,
    int GraceMinutes,
    bool IsNightShift,
    int HalfDayMinutes,
    int FullDayMinutes,
    int OvertimeThresholdMinutes,
    int DurationMinutes,
    string? Color,
    bool IsActive,
    uint Version);

/// <summary>Half/full-day thresholds default to half the net shift and the net shift minus 30 minutes.</summary>
public sealed record SaveShiftRequest(
    string Code,
    string Name,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int BreakMinutes,
    int GraceMinutes,
    bool IsNightShift,
    int? HalfDayMinutes,
    int? FullDayMinutes,
    int OvertimeThresholdMinutes,
    string? Color,
    bool IsActive,
    uint? Version);

public sealed record RosterEntryDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    DateOnly Date,
    Guid? ShiftId,
    string? ShiftCode,
    string? ShiftName,
    string? Color,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    bool IsWeekOff,
    string? Notes);

public sealed record RosterAssignmentInput(Guid EmployeeId, DateOnly Date, Guid? ShiftId, bool IsWeekOff, string? Notes);

/// <summary>
/// Saves roster cells. With <see cref="ReplaceExisting"/> (the roster-grid default) the cells sent replace whatever was
/// rostered for those employee-dates. Warnings must be acknowledged; errors never save.
/// </summary>
public sealed record SaveRosterRequest(IReadOnlyList<RosterAssignmentInput> Assignments, bool ReplaceExisting = true, bool AcknowledgeWarnings = false);

public sealed record CopyRosterWeekRequest(DateOnly SourceWeekStart, DateOnly TargetWeekStart, Guid? DepartmentId, bool AcknowledgeWarnings = false);

public sealed record RosterConflictDto(Guid EmployeeId, string? EmployeeName, DateOnly Date, RosterConflictKind Kind, ConflictSeverity Severity, string Message);

public sealed record RosterCheckResult(bool CanSave, bool HasWarnings, IReadOnlyList<RosterConflictDto> Conflicts);

public sealed record RosterSaveResult(int Saved, int Replaced, IReadOnlyList<RosterConflictDto> Warnings);

public sealed class SaveShiftRequestValidator : AbstractValidator<SaveShiftRequest>
{
    public SaveShiftRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Must(c => c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            .WithMessage("Use letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.BreakMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.GraceMinutes).InclusiveBetween(0, 120);
        RuleFor(x => x.OvertimeThresholdMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.HalfDayMinutes).InclusiveBetween(30, 24 * 60);
        RuleFor(x => x.FullDayMinutes).InclusiveBetween(30, 24 * 60);
        RuleFor(x => x)
            .Must(x => x.HalfDayMinutes is null || x.FullDayMinutes is null || x.HalfDayMinutes < x.FullDayMinutes)
            .WithName("halfDayMinutes").WithMessage("A half day must be shorter than a full day.");
        RuleFor(x => x)
            .Must(x => new ShiftTimes(x.StartTime, x.EndTime, x.BreakMinutes, 0, 0, 0, 0).NetMinutes > 0)
            .WithName("breakMinutes").WithMessage("The break is as long as the shift.");
        RuleFor(x => x.Color).Matches("^#[0-9A-Fa-f]{6}$").When(x => !string.IsNullOrEmpty(x.Color)).WithMessage("Use a colour like #1A73E8.");
    }
}

public sealed class SaveRosterRequestValidator : AbstractValidator<SaveRosterRequest>
{
    public const int MaxAssignments = 2000;
    public const int MaxSpanDays = 62;

    public SaveRosterRequestValidator()
    {
        RuleFor(x => x.Assignments).NotEmpty().Must(a => a.Count <= MaxAssignments).WithMessage($"Save at most {MaxAssignments} roster cells at a time.");
        RuleFor(x => x.Assignments)
            .Must(a => a.Count == 0 || a.Max(c => c.Date).DayNumber - a.Min(c => c.Date).DayNumber < MaxSpanDays)
            .WithMessage($"A roster change can cover at most {MaxSpanDays} days.");
        RuleForEach(x => x.Assignments).ChildRules(cell =>
        {
            cell.RuleFor(c => c.EmployeeId).NotEmpty();
            cell.RuleFor(c => c).Must(c => (c.ShiftId is not null) ^ c.IsWeekOff).WithName("shiftId")
                .WithMessage("Choose either a shift or a week off.");
            cell.RuleFor(c => c.Notes).SafeText(300);
        });
    }
}

public sealed class CopyRosterWeekRequestValidator : AbstractValidator<CopyRosterWeekRequest>
{
    public CopyRosterWeekRequestValidator()
    {
        RuleFor(x => x.TargetWeekStart).NotEqual(x => x.SourceWeekStart).WithMessage("Choose a different week to copy into.");
        RuleFor(x => x.TargetWeekStart)
            .Must((x, target) => Math.Abs(target.DayNumber - x.SourceWeekStart.DayNumber) % 7 == 0)
            .WithMessage("Both weeks must start on the same weekday.");
    }
}
