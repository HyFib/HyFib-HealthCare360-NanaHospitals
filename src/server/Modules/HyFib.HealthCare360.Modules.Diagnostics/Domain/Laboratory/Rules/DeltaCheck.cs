namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>Comparison of a result with the patient's previous verified result for the same analyte.</summary>
public sealed record DeltaOutcome(decimal? PreviousValue, DateTimeOffset? PreviousAt, decimal? Percent, bool Exceeded)
{
    public static DeltaOutcome None { get; } = new(null, null, null, false);
}

/// <summary>
/// Delta check (LIS-04, pure; unit-tested). A large change from the patient's last result points to a
/// mislabelled or contaminated sample as often as to a real clinical change. So a failed delta check only blocks
/// auto-validation and highlights the result; it never changes the value.
/// </summary>
internal static class DeltaCheck
{
    /// <summary>
    /// Delta % = |current − previous| ÷ |previous| × 100, rounded to one decimal. A change away from a previous value of
    /// zero has no percentage and counts as exceeded. A threshold of zero or less disables the check.
    /// </summary>
    public static DeltaOutcome Evaluate(decimal? current, decimal? previous, DateTimeOffset? previousAt, decimal thresholdPercent)
    {
        if (current is not { } now || previous is not { } before)
        {
            return DeltaOutcome.None;
        }

        var enabled = thresholdPercent > 0;
        var difference = Math.Abs(now - before);
        if (before == 0)
        {
            return new DeltaOutcome(before, previousAt, difference == 0 ? 0 : null, enabled && difference != 0);
        }

        var percent = Math.Round(difference / Math.Abs(before) * 100m, 1, MidpointRounding.AwayFromZero);
        return new DeltaOutcome(before, previousAt, percent, enabled && percent > thresholdPercent);
    }
}
