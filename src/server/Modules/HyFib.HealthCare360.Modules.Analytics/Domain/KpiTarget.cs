namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>Where a KPI stands against its facility's target: on target, needs attention, or breached.</summary>
public enum KpiStatus
{
    Green = 0,
    Amber,
    Red,
}

/// <summary>
/// A facility's target for one executive KPI, set by an administrator (audited by the platform's data audit, as every
/// change to an auditable record is). The value is the goal; the amber and red thresholds say when the KPI needs attention
/// and when it is breached. For a KPI where higher is better the thresholds lie below the target (red ≤ amber ≤ target),
/// for one where lower is better above it (red ≥ amber ≥ target). Counts and money (OP visits, admissions, revenue,
/// collections) are set per month and prorated to the period shown (<see cref="KpiTargetMath.Prorate"/>); rates, days,
/// minutes and scores are compared as they are.
/// </summary>
public sealed class KpiTarget : AuditableEntity, IFacilityScoped
{
    public const int MaxRoleLength = 64;

    public Guid FacilityId { get; set; }

    /// <summary>The KPI's key in the catalogue (<c>opVisits</c>, <c>occupancy</c>…).</summary>
    public string KpiKey { get; set; } = string.Empty;

    public decimal Target { get; set; }
    public decimal AmberThreshold { get; set; }
    public decimal RedThreshold { get; set; }

    /// <summary>The role told when the KPI goes amber or red (a system or custom role's name).</summary>
    public string OwnerRole { get; set; } = string.Empty;

    /// <summary>Who set it, as shown on the targets screen.</summary>
    public string? SetByName { get; set; }

    /// <summary>The status at the last month-to-date check, so the owner is told when it gets worse, not at every check.</summary>
    public KpiStatus? LastStatus { get; set; }

    public decimal? LastValue { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>A KPI's target as it applies to one period: the goal and thresholds after proration.</summary>
public sealed record KpiTargetBand(decimal Target, decimal Amber, decimal Red);

/// <summary>Target arithmetic (unit-tested with worked examples in <c>KpiTargetMathTests</c>).</summary>
public static class KpiTargetMath
{
    /// <summary>Units whose figures add up over days, so a monthly target is prorated to the period.</summary>
    public static bool IsAdditive(string unit) => unit is "count" or "inr";

    /// <summary>
    /// A monthly figure prorated to <paramref name="days"/> days at twelve months a year: monthly × days × 12 ÷ 365.
    /// ₹30,00,000 a month over 10 days is ₹9,86,301.37; over 365 days, ₹3,60,00,000.
    /// </summary>
    public static decimal Prorate(decimal monthly, int days) =>
        days <= 0 ? 0m : Math.Round(monthly * days * 12m / 365m, 2, MidpointRounding.AwayFromZero);

    /// <summary>The target and thresholds for a period of <paramref name="days"/> days.</summary>
    public static KpiTargetBand ForPeriod(string unit, decimal target, decimal amber, decimal red, int days) =>
        IsAdditive(unit)
            ? new KpiTargetBand(Prorate(target, days), Prorate(amber, days), Prorate(red, days))
            : new KpiTargetBand(target, amber, red);

    /// <summary>
    /// Green when the value is at or better than the amber threshold; amber when it is worse than that but not as bad as
    /// the red threshold; red when it reaches the red threshold or worse. No value (no data yet) has no status.
    /// Higher is better, amber 90, red 80: 92 → green, 90 → green, 85 → amber, 80 → red. Lower is better (ALOS), amber 5,
    /// red 6: 4.8 → green, 5.5 → amber, 6 → red.
    /// </summary>
    public static KpiStatus? Status(decimal? value, KpiTargetBand band, bool higherIsBetter)
    {
        if (value is not { } v)
        {
            return null;
        }

        if (higherIsBetter)
        {
            return v >= band.Amber ? KpiStatus.Green : v > band.Red ? KpiStatus.Amber : KpiStatus.Red;
        }

        return v <= band.Amber ? KpiStatus.Green : v < band.Red ? KpiStatus.Amber : KpiStatus.Red;
    }

    /// <summary>Whether the owner should be told: the KPI moved to a worse status than at the last check.</summary>
    public static bool IsNewBreach(KpiStatus? previous, KpiStatus? current) =>
        current is KpiStatus.Amber or KpiStatus.Red && (previous ?? KpiStatus.Green) < current;

    /// <summary>
    /// The thresholds must lie on the bad side of the target, red beyond amber: red ≤ amber ≤ target when higher is better,
    /// red ≥ amber ≥ target when lower is better. Returns the message for the field at fault, or null when they are in order.
    /// </summary>
    public static (string Field, string Message)? CheckOrder(decimal target, decimal amber, decimal red, bool higherIsBetter)
    {
        if (higherIsBetter)
        {
            if (amber > target)
            {
                return ("amberThreshold", "Higher is better for this KPI, so the amber threshold must be at or below the target.");
            }

            if (red > amber)
            {
                return ("redThreshold", "The red threshold must be at or below the amber threshold.");
            }
        }
        else
        {
            if (amber < target)
            {
                return ("amberThreshold", "Lower is better for this KPI, so the amber threshold must be at or above the target.");
            }

            if (red < amber)
            {
                return ("redThreshold", "The red threshold must be at or above the amber threshold.");
            }
        }

        return null;
    }
}
