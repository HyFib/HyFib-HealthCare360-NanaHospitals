namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum FluidDirection
{
    Intake = 0,
    Output,
}

public enum FluidRoute
{
    Oral = 0,
    IntravenousFluid,
    IntravenousMedication,
    BloodProduct,
    TubeFeed,
    Urine,
    Drain,
    Vomitus,
    Stool,
    NasogastricAspirate,
    BloodLoss,
    Other,
}

/// <summary>One measured intake or output on the fluid balance chart.</summary>
public sealed class IntakeOutputEntry : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }

    /// <summary>When the fluid went in or came out (not when it was typed).</summary>
    public DateTimeOffset RecordedAt { get; set; }

    public FluidDirection Direction { get; set; }
    public FluidRoute Route { get; set; }
    public int VolumeMl { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public Guid? RecordedBy { get; set; }
    public string? RecordedByName { get; set; }
    public string? VoidReason { get; set; }
}

public sealed record FluidEntry(DateTimeOffset At, FluidDirection Direction, FluidRoute Route, int VolumeMl);

public sealed record FluidTotals(int IntakeMl, int OutputMl)
{
    public int BalanceMl => IntakeMl - OutputMl;
}

public sealed record ShiftFluidTotals(NursingShift Shift, int IntakeMl, int OutputMl, int BalanceMl);

public sealed record RouteFluidTotal(FluidRoute Route, FluidDirection Direction, int VolumeMl);

public sealed record FluidBalanceSummary(DateTimeOffset From, DateTimeOffset To, FluidTotals Total, IReadOnlyList<ShiftFluidTotals> ByShift, IReadOnlyList<RouteFluidTotal> ByRoute);

/// <summary>
/// Fluid balance arithmetic. The chart day runs 08:00 to 08:00 IST (the nursing day), with shift-wise subtotals so a
/// shift can hand over its own balance; entries outside the window are ignored.
/// </summary>
public static class FluidBalance
{
    public static (DateTimeOffset From, DateTimeOffset To) ChartDay(DateOnly dutyDate) =>
        (IndiaClock.AtLocal(dutyDate, NursingShifts.MorningStart), IndiaClock.AtLocal(dutyDate.AddDays(1), NursingShifts.MorningStart));

    public static bool RouteMatches(FluidDirection direction, FluidRoute route) => route switch
    {
        FluidRoute.Oral or FluidRoute.IntravenousFluid or FluidRoute.IntravenousMedication or FluidRoute.BloodProduct or FluidRoute.TubeFeed
            => direction == FluidDirection.Intake,
        FluidRoute.Other => true,
        _ => direction == FluidDirection.Output,
    };

    public static FluidBalanceSummary Summarize(IEnumerable<FluidEntry> entries, DateTimeOffset from, DateTimeOffset to)
    {
        var inWindow = entries.Where(e => e.At >= from && e.At < to).ToList();
        var total = Totals(inWindow);
        var byShift = Enum.GetValues<NursingShift>()
            .Select(shift =>
            {
                var t = Totals(inWindow.Where(e => NursingShifts.At(e.At) == shift));
                return new ShiftFluidTotals(shift, t.IntakeMl, t.OutputMl, t.BalanceMl);
            })
            .ToList();
        var byRoute = inWindow
            .GroupBy(e => new { e.Route, e.Direction })
            .Select(g => new RouteFluidTotal(g.Key.Route, g.Key.Direction, g.Sum(e => e.VolumeMl)))
            .OrderBy(r => r.Direction).ThenBy(r => r.Route)
            .ToList();
        return new FluidBalanceSummary(from, to, total, byShift, byRoute);
    }

    private static FluidTotals Totals(IEnumerable<FluidEntry> entries)
    {
        var list = entries.ToList();
        return new FluidTotals(
            list.Where(e => e.Direction == FluidDirection.Intake).Sum(e => e.VolumeMl),
            list.Where(e => e.Direction == FluidDirection.Output).Sum(e => e.VolumeMl));
    }
}
