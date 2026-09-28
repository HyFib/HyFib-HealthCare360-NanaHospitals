namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>
/// Hospital MIS formulas (pure and unit-tested) as used by Indian hospitals and NABH indicator reporting. Every rate
/// returns <c>null</c> when its denominator is zero so a quiet day shows "—" instead of a misleading 0 %.
/// Rounding is half away from zero.
/// </summary>
public static class KpiMath
{
    /// <summary>part ÷ whole × 100.</summary>
    public static decimal? Percent(decimal part, decimal whole, int decimals = 1) =>
        whole == 0 ? null : Math.Round(part * 100m / whole, decimals, MidpointRounding.AwayFromZero);

    /// <summary>a ÷ b, or null when b is zero.</summary>
    public static decimal? Ratio(decimal numerator, decimal denominator, int decimals = 2) =>
        denominator == 0 ? null : Math.Round(numerator / denominator, decimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Bed occupancy rate: inpatient days (patients in a bed at each midnight census) ÷ (beds in service × days) × 100.
    /// </summary>
    public static decimal? OccupancyPercent(decimal inpatientDays, int bedsInService, int days) =>
        bedsInService <= 0 || days <= 0 ? null : Percent(inpatientDays, (decimal)bedsInService * days);

    /// <summary>
    /// Length of stay as reported in MIS: calendar days between the IST admission and discharge dates, at least one day
    /// (a same-day admission counts as one day).
    /// </summary>
    public static int LengthOfStayDays(DateOnly admitted, DateOnly discharged) => Math.Max(1, discharged.DayNumber - admitted.DayNumber);

    /// <summary>Average length of stay (ALOS): total length of stay of discharged patients ÷ discharges, one decimal.</summary>
    public static decimal? AverageLengthOfStay(long totalStayDays, int discharges) =>
        discharges <= 0 ? null : Math.Round((decimal)totalStayDays / discharges, 1, MidpointRounding.AwayFromZero);

    /// <summary>Bed turnover rate: discharges (including deaths) ÷ beds in service — how many patients used each bed.</summary>
    public static decimal? BedTurnoverRate(int discharges, int bedsInService) =>
        bedsInService <= 0 ? null : Math.Round((decimal)discharges / bedsInService, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Bed turnover interval: (available bed-days − inpatient days) ÷ discharges — the average days a bed stays empty
    /// between two patients. Never negative (an over-occupied ward has no idle interval).
    /// </summary>
    public static decimal? BedTurnoverInterval(int bedsInService, int days, decimal inpatientDays, int discharges) =>
        discharges <= 0 || bedsInService <= 0 || days <= 0
            ? null
            : Math.Round(Math.Max(0m, ((decimal)bedsInService * days) - inpatientDays) / discharges, 1, MidpointRounding.AwayFromZero);

    /// <summary>Gross death rate: deaths ÷ discharges (including deaths) × 100, two decimals.</summary>
    public static decimal? GrossDeathRate(int deaths, int discharges) => Percent(deaths, discharges, 2);

    /// <summary>
    /// Net death rate: (deaths − deaths within 48 hours of admission) ÷ (discharges − deaths within 48 hours) × 100 —
    /// excludes patients who died before the hospital could influence the outcome.
    /// </summary>
    public static decimal? NetDeathRate(int deaths, int deathsWithin48Hours, int discharges) =>
        Percent(deaths - deathsWithin48Hours, discharges - deathsWithin48Hours, 2);

    /// <summary>Net Promoter Score: % promoters (9–10) − % detractors (0–6), from −100 to +100, one decimal.</summary>
    public static decimal? NetPromoterScore(int promoters, int passives, int detractors)
    {
        var responses = promoters + passives + detractors;
        return responses <= 0 ? null : Math.Round((promoters - detractors) * 100m / responses, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>Change from the previous period in percent (null when there is no previous value to compare with).</summary>
    public static decimal? ChangePercent(decimal? current, decimal? previous) =>
        current is not { } now || previous is not { } before || before == 0
            ? null
            : Math.Round((now - before) * 100m / Math.Abs(before), 1, MidpointRounding.AwayFromZero);

    /// <summary>Appointment no-show rate: no-shows ÷ appointments that were due (attended + no-show) × 100.</summary>
    public static decimal? NoShowRate(int noShows, int attended) => Percent(noShows, noShows + attended);

    /// <summary>Incident rate per 1,000 patient-days (NABH style).</summary>
    public static decimal? PerThousand(decimal events, decimal exposure) =>
        exposure <= 0 ? null : Math.Round(events * 1000m / exposure, 2, MidpointRounding.AwayFromZero);

    /// <summary>Gross margin %: (sales − cost) ÷ sales × 100.</summary>
    public static decimal? MarginPercent(decimal sales, decimal cost) => sales == 0 ? null : Percent(sales - cost, sales);
}
