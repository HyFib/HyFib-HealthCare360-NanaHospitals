namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>Turnaround-time summary in minutes.</summary>
public sealed record TurnaroundSummary(int Count, double? AverageMinutes, double? MedianMinutes, double? P90Minutes, double? WithinTargetPercent);

/// <summary>
/// Turnaround-time statistics for lab, radiology and other workflows (LIS-07, IMG-04). The median and 90th percentile
/// describe the patient's experience better than the mean, which a few outliers (outsourced or repeated tests) distort.
/// Both use the nearest-rank method (as the Diagnostics module does), so every figure is a real, observed turnaround.
/// Negative, NaN and infinite durations (clock errors, missing milestones) are ignored.
/// </summary>
public static class Percentiles
{
    /// <summary>Nearest-rank percentile (0–100] of a list sorted ascending; null for an empty list.</summary>
    public static double? NearestRank(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * sortedAscending.Count);
        return sortedAscending[Math.Clamp(rank - 1, 0, sortedAscending.Count - 1)];
    }

    /// <summary>Count, mean, median, 90th percentile and (optionally) the share within a target, rounded to 0.1 minute.</summary>
    public static TurnaroundSummary Summarize(IEnumerable<double> minutes, double? targetMinutes = null)
    {
        var values = minutes.Where(m => m >= 0 && !double.IsNaN(m) && !double.IsInfinity(m)).Order().ToList();
        if (values.Count == 0)
        {
            return new TurnaroundSummary(0, null, null, null, null);
        }

        double? withinTarget = targetMinutes is { } target
            ? Math.Round(values.Count(v => v <= target) * 100.0 / values.Count, 1, MidpointRounding.AwayFromZero)
            : null;
        return new TurnaroundSummary(
            values.Count,
            Math.Round(values.Average(), 1, MidpointRounding.AwayFromZero),
            Round(NearestRank(values, 50)),
            Round(NearestRank(values, 90)),
            withinTarget);
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1, MidpointRounding.AwayFromZero) : null;
}
