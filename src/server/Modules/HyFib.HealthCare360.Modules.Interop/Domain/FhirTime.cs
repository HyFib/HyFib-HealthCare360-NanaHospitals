using System.Globalization;

namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>
/// Date and time conventions of the facade. Instants are stored in UTC and written in India Standard Time (+05:30, no
/// daylight saving) because receiving clinicians read them as local times. Clinical times are cut to whole seconds.
/// <c>meta.lastUpdated</c> keeps milliseconds so that clients polling with <c>_lastUpdated</c> do not skip rows.
/// Calendar dates in search values and in the data (date of birth, onset) are IST dates.
/// </summary>
internal static class FhirTime
{
    public static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    /// <summary>The instant in IST, truncated to whole seconds.</summary>
    public static DateTimeOffset ToSeconds(DateTimeOffset value)
    {
        var ist = value.ToOffset(Ist);
        return new DateTimeOffset(ist.Ticks - (ist.Ticks % TimeSpan.TicksPerSecond), Ist);
    }

    /// <summary>The instant in IST, truncated to milliseconds (<c>meta.lastUpdated</c>, <c>issued</c>).</summary>
    public static DateTimeOffset ToMilliseconds(DateTimeOffset value)
    {
        var ist = value.ToOffset(Ist);
        return new DateTimeOffset(ist.Ticks - (ist.Ticks % TimeSpan.TicksPerMillisecond), Ist);
    }

    /// <summary>FHIR dateTime text: <c>2026-09-01T10:00:15+05:30</c>.</summary>
    public static string DateTime(DateTimeOffset value) =>
        ToSeconds(value).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>FHIR date text: <c>2026-09-01</c>.</summary>
    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>IST midnight at the start of a calendar date.</summary>
    public static DateTimeOffset StartOfDay(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), Ist);
}
