using System.Globalization;

namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>How bed days are counted for charging (facility setting <c>inpatient.bedDayPolicy</c>).</summary>
public enum BedDayPolicy
{
    /// <summary>A day is charged when the patient is in a bed at local midnight; a stay that crosses no midnight is charged one day.</summary>
    MidnightCensus = 0,

    /// <summary>Every calendar day (or part of one) from admission to discharge is charged.</summary>
    CalendarDay,
}

/// <summary>
/// A period in one bed (UTC instants) with the per-day bed charge that applies to it, and whether the bed was in critical
/// care when the stay began there (<see cref="BedAssignment.IsCriticalCare"/>).
/// </summary>
public sealed record BedStay(DateTimeOffset From, DateTimeOffset? To, Guid? BedServiceItemId, Guid WardId, bool IsCriticalCare = false);

/// <summary>A chargeable bed day, the bed class (service item) to charge for it, and whether the bed was in critical care.</summary>
public sealed record BedDay(DateOnly Date, Guid? BedServiceItemId, Guid? WardId, bool IsCriticalCare = false);

/// <summary>
/// Works out which in-patient days are chargeable (IPB auto bed/nursing charges). Pure and deterministic so the hourly
/// job, the discharge flow and the tests always agree on the same days; the bed class of a day comes from the bed the
/// patient occupied at that day's census point, so transfers are priced correctly without any special handling.
/// </summary>
public static class BedDayCalculator
{
    /// <param name="admittedAt">Admission instant.</param>
    /// <param name="endedAt">Discharge instant, or the moment discharge is being processed; null while the stay continues
    /// (then only census points already passed are chargeable).</param>
    /// <param name="stays">Bed assignments of the admission.</param>
    /// <param name="asOf">Now.</param>
    /// <param name="policy">Counting policy.</param>
    public static IReadOnlyList<BedDay> DueDays(
        DateTimeOffset admittedAt, DateTimeOffset? endedAt, IReadOnlyList<BedStay> stays, DateTimeOffset asOf, BedDayPolicy policy)
    {
        var stop = endedAt is { } ended && ended < asOf ? ended : asOf;
        if (stop < admittedAt)
        {
            return [];
        }

        var first = IndiaClock.LocalDate(admittedAt);
        var last = IndiaClock.LocalDate(stop);
        var days = new List<BedDay>();

        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var nextMidnight = IndiaClock.StartOfDayUtc(date.AddDays(1));
            if (policy == BedDayPolicy.MidnightCensus)
            {
                if (nextMidnight > stop)
                {
                    break;
                }

                days.Add(Day(date, stays, nextMidnight));
            }
            else
            {
                days.Add(Day(date, stays, nextMidnight < stop ? nextMidnight : stop));
            }
        }

        if (days.Count == 0 && endedAt is not null)
        {
            // Short stay that closed without crossing a midnight: charged as one day.
            days.Add(Day(first, stays, stop));
        }

        return days;
    }

    /// <summary>The bed occupied just before <paramref name="instant"/> (a transfer exactly at midnight counts for the next day).</summary>
    public static BedStay? StayAt(IReadOnlyList<BedStay> stays, DateTimeOffset instant) =>
        stays.Where(s => s.From < instant).OrderByDescending(s => s.From).FirstOrDefault()
        ?? stays.OrderBy(s => s.From).FirstOrDefault();

    private static BedDay Day(DateOnly date, IReadOnlyList<BedStay> stays, DateTimeOffset censusPoint)
    {
        var stay = StayAt(stays, censusPoint);
        return new BedDay(date, stay?.BedServiceItemId, stay?.WardId, stay?.IsCriticalCare ?? false);
    }
}

/// <summary>
/// Source keys of charges posted to Billing. Posting is idempotent on (SourceModule, SourceReference), so these keys must
/// be stable across retries, restarts and cultures: "{admissionId}:{yyyy-MM-dd}" per admission per day, with bed and
/// nursing charges under different source modules so the two charges of one day never collide.
/// </summary>
public static class DailyChargeKeys
{
    public const string BedSource = "inpatient.bed";
    public const string NursingSource = "inpatient.nursing";
    public const string ConsultantVisitSource = "inpatient.visit";

    public static string For(Guid admissionId, DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{admissionId:D}:{date:yyyy-MM-dd}");

    public static string SourceFor(DailyChargeKind kind) => kind == DailyChargeKind.Bed ? BedSource : NursingSource;
}
