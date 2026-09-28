namespace HyFib.HealthCare360.Modules.Diagnostics.Domain;

/// <summary>Turnaround summary in minutes.</summary>
public sealed record TatSummary(int Count, double? AverageMinutes, double? MedianMinutes, double? P90Minutes);

/// <summary>
/// Turnaround-time statistics (LIS-07, IMG-04; pure, unit-tested). The median and 90th percentile describe the
/// patient's experience better than the mean, which a few outliers (outsourced or repeated tests) distort. Both use the
/// nearest-rank method, so every reported figure is a real, observed turnaround.
/// </summary>
internal static class TatStatistics
{
    public static TatSummary Summarize(IEnumerable<double> minutes)
    {
        var values = minutes.Where(m => m >= 0 && !double.IsNaN(m) && !double.IsInfinity(m)).Order().ToList();
        return values.Count == 0
            ? new TatSummary(0, null, null, null)
            : new TatSummary(values.Count, Math.Round(values.Average(), 1), Percentile(values, 50), Percentile(values, 90));
    }

    /// <summary>Nearest-rank percentile of an ascending list.</summary>
    internal static double Percentile(IReadOnlyList<double> sorted, int percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        return Math.Round(sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)], 1);
    }

    /// <summary>Minutes between two milestones, or null when either is missing or they are out of order.</summary>
    public static double? Minutes(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } start && to is { } end && end >= start ? (end - start).TotalMinutes : null;
}
