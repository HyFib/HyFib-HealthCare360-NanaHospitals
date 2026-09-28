namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// A duty shift (Morning 07:00–14:00, Night 21:00–07:00…). The attendance thresholds live on the shift because a
/// 12-hour ICU night and a 6-hour OPD shift cannot share one "full day" definition.
/// </summary>
public sealed class Shift : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int BreakMinutes { get; set; }

    /// <summary>Arrivals up to this many minutes after the start are not marked late.</summary>
    public int GraceMinutes { get; set; } = 10;

    /// <summary>Night-shift flag for night allowance and fatigue rules (independent of crossing midnight).</summary>
    public bool IsNightShift { get; set; }

    /// <summary>Minimum net worked minutes for a half day.</summary>
    public int HalfDayMinutes { get; set; }

    /// <summary>Minimum net worked minutes for a full day.</summary>
    public int FullDayMinutes { get; set; }

    /// <summary>Extra minutes below this are not counted as overtime.</summary>
    public int OvertimeThresholdMinutes { get; set; } = 30;

    /// <summary>Roster colour (#RRGGBB) so the roster grid is readable at a glance.</summary>
    public string? Color { get; set; }

    public bool IsActive { get; set; } = true;

    public ShiftTimes ToTimes() => new(StartTime, EndTime, BreakMinutes, GraceMinutes, HalfDayMinutes, FullDayMinutes, OvertimeThresholdMinutes);
}

/// <summary>The time rules of a shift, as used by the pure attendance and roster calculators.</summary>
public sealed record ShiftTimes(
    TimeOnly Start,
    TimeOnly End,
    int BreakMinutes,
    int GraceMinutes,
    int HalfDayMinutes,
    int FullDayMinutes,
    int OvertimeThresholdMinutes)
{
    /// <summary>End at or before start means the shift finishes on the next calendar day.</summary>
    public bool CrossesMidnight => End <= Start;

    /// <summary>Scheduled length; equal start and end means a 24-hour duty.</summary>
    public int DurationMinutes => End == Start ? 24 * 60 : (int)(End - Start).TotalMinutes;

    /// <summary>Scheduled length without the break (the basis for overtime).</summary>
    public int NetMinutes => Math.Max(0, DurationMinutes - BreakMinutes);

    /// <summary>Local start of this shift when rostered on <paramref name="date"/>.</summary>
    public DateTime StartOn(DateOnly date) => date.ToDateTime(Start);

    /// <summary>Local end of this shift when rostered on <paramref name="date"/> (next day for night shifts).</summary>
    public DateTime EndOn(DateOnly date) => StartOn(date).AddMinutes(DurationMinutes);

    /// <summary>
    /// Several shifts on one day (a "double duty") behave as one continuous duty from the first start to the last end,
    /// with the breaks added up and the first shift's thresholds.
    /// </summary>
    public static ShiftTimes Combine(IReadOnlyList<ShiftTimes> shifts)
    {
        if (shifts.Count == 0)
        {
            throw new ArgumentException("At least one shift is required.", nameof(shifts));
        }

        if (shifts.Count == 1)
        {
            return shifts[0];
        }

        var ordered = shifts.OrderBy(s => s.Start).ToList();
        var first = ordered[0];
        var anchor = new DateOnly(2000, 1, 1);
        var lastEnd = ordered.Max(s => s.EndOn(anchor));
        var totalMinutes = (int)(lastEnd - first.StartOn(anchor)).TotalMinutes;

        // A combined duty of 24 hours or more is modelled as a 24-hour duty (end == start).
        var end = totalMinutes >= 24 * 60 ? first.Start : first.Start.AddMinutes(totalMinutes);
        return first with { End = end, BreakMinutes = ordered.Sum(s => s.BreakMinutes) };
    }
}
