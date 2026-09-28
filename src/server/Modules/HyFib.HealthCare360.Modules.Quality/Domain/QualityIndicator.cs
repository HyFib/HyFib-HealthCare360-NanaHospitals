using System.Globalization;

namespace HyFib.HealthCare360.Modules.Quality.Domain;

public enum IndicatorUnit
{
    Percent = 0,

    /// <summary>Per 1,000 (patient-days, device-days, discharges…).</summary>
    PerThousand,
    Minutes,
    Hours,
    Days,
    Ratio,
    Count,
}

public enum IndicatorDirection
{
    HigherIsBetter = 0,
    LowerIsBetter,
}

public enum IndicatorFrequency
{
    Monthly = 0,
    Quarterly,

    /// <summary>Indian financial year (April–March).</summary>
    Annually,
}

public enum TrendDirection
{
    InsufficientData = 0,
    Improving,
    Stable,
    Worsening,
}

/// <summary>
/// A quality indicator definition (IRMS-06), e.g. NABH "patient fall rate per 1,000 patient-days". Definitions are
/// group-wide so branches report the same measure the same way.
/// </summary>
public sealed class QualityIndicator : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string NumeratorDefinition { get; set; } = string.Empty;
    public string DenominatorDefinition { get; set; } = string.Empty;
    public IndicatorUnit Unit { get; set; }

    /// <summary>Scales numerator ÷ denominator (100 for percentages, 1,000 for per-thousand rates, 1 for averages).</summary>
    public decimal Multiplier { get; set; } = 1;

    public decimal? Target { get; set; }
    public IndicatorDirection Direction { get; set; }
    public IndicatorFrequency Frequency { get; set; }

    /// <summary>NABH standard reference (for example "PSQ.3").</summary>
    public string? NabhReference { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A measured value for one period and facility.</summary>
public sealed class IndicatorValue : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid IndicatorId { get; set; }

    /// <summary>First day of the month, quarter or financial year measured.</summary>
    public DateOnly PeriodStart { get; set; }

    public decimal Numerator { get; set; }
    public decimal Denominator { get; set; }
    public decimal Value { get; set; }

    /// <summary>Evaluated against the target in force when the value was recorded.</summary>
    public bool? MeetsTarget { get; set; }

    public string? Notes { get; set; }
}

/// <summary>Pure indicator arithmetic (value, target evaluation, periods and trend).</summary>
public static class IndicatorMath
{
    /// <summary>Change beyond this share of the recent average counts as improving or worsening.</summary>
    public const decimal TrendThreshold = 0.05m;

    public static decimal DefaultMultiplier(IndicatorUnit unit) => unit switch
    {
        IndicatorUnit.Percent => 100,
        IndicatorUnit.PerThousand => 1000,
        _ => 1,
    };

    /// <summary>numerator ÷ denominator × multiplier, to 2 decimals; null when the denominator is zero.</summary>
    public static decimal? Compute(decimal numerator, decimal denominator, decimal multiplier) =>
        denominator == 0 ? null : Math.Round(numerator / denominator * multiplier, 2, MidpointRounding.AwayFromZero);

    public static bool? MeetsTarget(decimal? value, decimal? target, IndicatorDirection direction) =>
        value is not { } v || target is not { } t ? null
        : direction == IndicatorDirection.HigherIsBetter ? v >= t : v <= t;

    /// <summary>Normalises any date to the start of its reporting period (quarters: Jan/Apr/Jul/Oct; years: 1 April).</summary>
    public static DateOnly PeriodStart(DateOnly date, IndicatorFrequency frequency) => frequency switch
    {
        IndicatorFrequency.Monthly => new DateOnly(date.Year, date.Month, 1),
        IndicatorFrequency.Quarterly => new DateOnly(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1),
        _ => date.Month >= 4 ? new DateOnly(date.Year, 4, 1) : new DateOnly(date.Year - 1, 4, 1),
    };

    public static string PeriodLabel(DateOnly periodStart, IndicatorFrequency frequency) => frequency switch
    {
        IndicatorFrequency.Monthly => periodStart.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        IndicatorFrequency.Quarterly => $"Q{((periodStart.Month - 1) / 3) + 1} {periodStart.Year}",
        _ => $"FY {periodStart.Year}-{(periodStart.Year + 1) % 100:00}",
    };

    /// <summary>
    /// Compares the latest value with the average of up to three previous values, in the indicator's direction.
    /// Values must be in chronological order.
    /// </summary>
    public static TrendDirection Trend(IReadOnlyList<decimal> values, IndicatorDirection direction)
    {
        if (values.Count < 2)
        {
            return TrendDirection.InsufficientData;
        }

        var latest = values[^1];
        var baseline = values.Take(values.Count - 1).TakeLast(3).Average();
        var change = latest - baseline;
        var tolerance = Math.Abs(baseline) * TrendThreshold;
        if (Math.Abs(change) <= tolerance)
        {
            return TrendDirection.Stable;
        }

        var better = direction == IndicatorDirection.HigherIsBetter ? change > 0 : change < 0;
        return better ? TrendDirection.Improving : TrendDirection.Worsening;
    }
}
