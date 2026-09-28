using System.Globalization;

namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>
/// Hospital calendar for MIS (IST, UTC+05:30, no daylight saving). Instants are stored in UTC; a reporting "day" is an
/// Indian calendar day, so every date filter becomes a half-open UTC range and every day/hour bucket is computed on the
/// instant shifted by <see cref="OffsetMinutes"/>. The shift is also used inside SQL
/// (<c>x.AddMinutes(AnalyticsClock.OffsetMinutes)</c> → <c>x + INTERVAL '330 mins'</c>), so C# and PostgreSQL agree on
/// every boundary without relying on the server's time-zone database.
/// </summary>
public static class AnalyticsClock
{
    /// <summary>Indian Standard Time offset in minutes (UTC+05:30).</summary>
    public const int OffsetMinutes = 330;

    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(OffsetMinutes);

    public static DateTimeOffset ToLocal(DateTimeOffset instant) => instant.ToOffset(Offset);

    /// <summary>The IST calendar date of an instant.</summary>
    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    public static DateOnly Today(TimeProvider time) => LocalDate(time.GetUtcNow());

    /// <summary>UTC instant of IST midnight at the start of <paramref name="date"/>.</summary>
    public static DateTimeOffset StartOfDay(DateOnly date) => At(date, TimeOnly.MinValue);

    /// <summary>UTC instant of an IST wall-clock time on an IST date.</summary>
    public static DateTimeOffset At(DateOnly date, TimeOnly time) => new DateTimeOffset(date.ToDateTime(time), Offset).ToUniversalTime();

    /// <summary>Half-open UTC range [start, end) covering the IST dates <paramref name="from"/>..<paramref name="to"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly from, DateOnly to) => (StartOfDay(from), StartOfDay(to.AddDays(1)));

    /// <summary>Number of calendar days in the inclusive range.</summary>
    public static int Days(DateOnly from, DateOnly to) => Math.Max(0, to.DayNumber - from.DayNumber + 1);

    public static string FormatDate(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    public static string FormatDateTime(DateTimeOffset instant) =>
        ToLocal(instant).ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture) + " IST";
}
