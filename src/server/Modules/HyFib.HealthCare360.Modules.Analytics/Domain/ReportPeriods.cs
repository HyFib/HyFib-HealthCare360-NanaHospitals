namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>A named reporting period ("last month") and its plain-language label.</summary>
public sealed record PeriodPreset(string Value, string Label);

/// <summary>
/// Relative reporting periods (IST calendar). Scheduled e-mails store the preset, not dates, so "yesterday" in a daily
/// e-mail is always the day before it is sent. Periods that are still running ("this month") end today, never in the
/// future. Weeks run Monday to Sunday; quarters are Jan–Mar, Apr–Jun, Jul–Sep and Oct–Dec (the same blocks as the
/// Indian financial-year quarters); the financial year runs 1 April to 31 March.
/// </summary>
public static class ReportPeriods
{
    public const string Today = "today";
    public const string Yesterday = "yesterday";
    public const string Last7Days = "last7Days";
    public const string Last30Days = "last30Days";
    public const string ThisWeek = "thisWeek";
    public const string LastWeek = "lastWeek";
    public const string ThisMonth = "thisMonth";
    public const string LastMonth = "lastMonth";
    public const string ThisQuarter = "thisQuarter";
    public const string LastQuarter = "lastQuarter";
    public const string ThisFinancialYear = "thisFinancialYear";
    public const string LastFinancialYear = "lastFinancialYear";

    /// <summary>Explicit <c>from</c>/<c>to</c> dates.</summary>
    public const string Custom = "custom";

    public static IReadOnlyList<PeriodPreset> All { get; } =
    [
        new(Today, "Today"),
        new(Yesterday, "Yesterday"),
        new(Last7Days, "Last 7 days"),
        new(Last30Days, "Last 30 days"),
        new(ThisWeek, "This week"),
        new(LastWeek, "Last week"),
        new(ThisMonth, "This month"),
        new(LastMonth, "Last month"),
        new(ThisQuarter, "This quarter"),
        new(LastQuarter, "Last quarter"),
        new(ThisFinancialYear, "This financial year"),
        new(LastFinancialYear, "Last financial year"),
        new(Custom, "Custom dates"),
    ];

    public static bool IsKnown(string? value) => value is not null && All.Any(p => string.Equals(p.Value, value, StringComparison.Ordinal));

    public static string LabelOf(string? value) => All.FirstOrDefault(p => string.Equals(p.Value, value, StringComparison.Ordinal))?.Label ?? "Custom dates";

    /// <summary>Resolves a preset to IST dates relative to <paramref name="today"/>; <see cref="Custom"/> is not resolvable.</summary>
    public static (DateOnly From, DateOnly To) Resolve(string preset, DateOnly today)
    {
        switch (preset)
        {
            case Today:
                return (today, today);
            case Yesterday:
                return (today.AddDays(-1), today.AddDays(-1));
            case Last7Days:
                return (today.AddDays(-6), today);
            case Last30Days:
                return (today.AddDays(-29), today);
            case ThisWeek:
                return (StartOfWeek(today), today);
            case LastWeek:
            {
                var start = StartOfWeek(today).AddDays(-7);
                return (start, start.AddDays(6));
            }

            case ThisMonth:
                return (new DateOnly(today.Year, today.Month, 1), today);
            case LastMonth:
            {
                var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                return (start, start.AddMonths(1).AddDays(-1));
            }

            case ThisQuarter:
                return (StartOfQuarter(today), today);
            case LastQuarter:
            {
                var start = StartOfQuarter(today).AddMonths(-3);
                return (start, start.AddMonths(3).AddDays(-1));
            }

            case ThisFinancialYear:
                return (StartOfFinancialYear(today), today);
            case LastFinancialYear:
            {
                var start = StartOfFinancialYear(today).AddYears(-1);
                return (start, start.AddYears(1).AddDays(-1));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "Not a relative period.");
        }
    }

    /// <summary>The period of the same length that ends the day before <paramref name="from"/> (for "vs previous" comparisons).</summary>
    public static (DateOnly From, DateOnly To) Previous(DateOnly from, DateOnly to)
    {
        var length = Math.Max(1, to.DayNumber - from.DayNumber + 1);
        var previousTo = from.AddDays(-1);
        return (previousTo.AddDays(-(length - 1)), previousTo);
    }

    /// <summary>"12 Sep 2026" or "01 Sep 2026 – 26 Sep 2026".</summary>
    public static string Describe(DateOnly from, DateOnly to) =>
        from == to ? AnalyticsClock.FormatDate(from) : $"{AnalyticsClock.FormatDate(from)} – {AnalyticsClock.FormatDate(to)}";

    /// <summary>The preset a scheduled e-mail uses by default: the complete period that just ended.</summary>
    public static string DefaultFor(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.Daily => Yesterday,
        ScheduleFrequency.Weekly => LastWeek,
        _ => LastMonth,
    };

    public static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateOnly StartOfQuarter(DateOnly date) => new(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1);

    public static DateOnly StartOfFinancialYear(DateOnly date) => date.Month >= 4 ? new DateOnly(date.Year, 4, 1) : new DateOnly(date.Year - 1, 4, 1);
}
