using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>
/// Validated report inputs. Dates are IST calendar dates; every other value is kept in its canonical string form so
/// the same arguments can be cached, stored with a schedule or a dashboard widget, and shown in an export header.
/// </summary>
public sealed class ReportArguments
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public ReportArguments(DateOnly from, DateOnly to, string? period, string? facility, IReadOnlyDictionary<string, string> values)
    {
        From = from;
        To = to;
        Period = period;
        Facility = facility;
        _values = values;
    }

    /// <summary>First IST date of the period (today for reports without a period).</summary>
    public DateOnly From { get; }

    /// <summary>Last IST date of the period (inclusive).</summary>
    public DateOnly To { get; }

    /// <summary>The preset that produced the dates (<see cref="ReportPeriods.Custom"/> for explicit dates), null without a period.</summary>
    public string? Period { get; }

    /// <summary>Requested facility: a facility id, <see cref="ReportKeys.AllFacilities"/>, or null for the signed-in facility.</summary>
    public string? Facility { get; }

    /// <summary>Other parameter values by key (department, payer, choices, numbers…).</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    public int DayCount => AnalyticsClock.Days(From, To);

    public DateTimeOffset Start => AnalyticsClock.StartOfDay(From);

    /// <summary>Exclusive UTC end of the period (the IST midnight after <see cref="To"/>).</summary>
    public DateTimeOffset End => AnalyticsClock.StartOfDay(To.AddDays(1));

    public Guid? DepartmentId => GuidOf(ReportKeys.Department);

    public Guid? PractitionerId => GuidOf(ReportKeys.Practitioner);

    public Guid? PayerId => GuidOf(ReportKeys.Payer);

    public Guid? WardId => GuidOf(ReportKeys.Ward);

    public Guid? StoreId => GuidOf(ReportKeys.Store);

    public Guid? TheatreId => GuidOf(ReportKeys.Theatre);

    public string? Text(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public Guid? GuidOf(string key) => _values.TryGetValue(key, out var value) && Guid.TryParse(value, out var id) ? id : null;

    public int? NumberOf(string key) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    public TEnum? EnumOf<TEnum>(string key)
        where TEnum : struct, Enum =>
        _values.TryGetValue(key, out var value) && Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    /// <summary>
    /// Canonical key/value form: stable order, the preset instead of dates when a preset was used. Used for cache keys,
    /// schedules and dashboard widgets.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToCanonical()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Period is not null)
        {
            result[ReportKeys.Period] = Period;
            if (Period == ReportPeriods.Custom)
            {
                result[ReportKeys.From] = From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                result[ReportKeys.To] = To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        if (Facility is not null)
        {
            result[ReportKeys.Facility] = Facility;
        }

        foreach (var (key, value) in _values)
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>A stable text form of the arguments with resolved dates (cache key component).</summary>
    public string CacheKey() =>
        string.Join('&', ToCanonical().Select(kv => $"{kv.Key}={kv.Value}"))
        + string.Create(CultureInfo.InvariantCulture, $"&@{From:yyyy-MM-dd}..{To:yyyy-MM-dd}");
}
