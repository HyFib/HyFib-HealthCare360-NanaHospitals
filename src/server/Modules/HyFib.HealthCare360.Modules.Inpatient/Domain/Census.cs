namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>Length of stay as reported in Indian hospital MIS: calendar days between admission and discharge, minimum one day.</summary>
public static class LengthOfStay
{
    public static int Days(DateTimeOffset admittedAt, DateTimeOffset end) =>
        Math.Max(1, IndiaClock.LocalDate(end).DayNumber - IndiaClock.LocalDate(admittedAt).DayNumber);
}

/// <summary>Bed counts by status with the bed occupancy rate.</summary>
public sealed record BedCounts(int Total, int Occupied, int Vacant, int Reserved, int Cleaning, int Blocked, int Maintenance)
{
    public static BedCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Beds that could take a patient (blocked and maintenance beds are out of service).</summary>
    public int Operational => Total - Blocked - Maintenance;

    /// <summary>Bed occupancy rate: occupied ÷ operational beds × 100, one decimal.</summary>
    public decimal OccupancyPercent => Operational <= 0 ? 0 : Math.Round(Occupied * 100m / Operational, 1, MidpointRounding.AwayFromZero);

    public static BedCounts From(IEnumerable<BedStatus> statuses)
    {
        var list = statuses.ToList();
        return new BedCounts(
            list.Count,
            list.Count(s => s == BedStatus.Occupied),
            list.Count(s => s == BedStatus.Vacant),
            list.Count(s => s == BedStatus.Reserved),
            list.Count(s => s == BedStatus.Cleaning),
            list.Count(s => s == BedStatus.Blocked),
            list.Count(s => s == BedStatus.Maintenance));
    }

    public static BedCounts Sum(IEnumerable<BedCounts> counts) => counts.Aggregate(Empty, (a, b) => new BedCounts(
        a.Total + b.Total, a.Occupied + b.Occupied, a.Vacant + b.Vacant, a.Reserved + b.Reserved, a.Cleaning + b.Cleaning,
        a.Blocked + b.Blocked, a.Maintenance + b.Maintenance));
}

/// <summary>An admission's timeline as the census needs it (cancelled admissions are excluded before calling).</summary>
public sealed record StayRecord(Guid AdmissionId, DateTimeOffset AdmittedAt, DateTimeOffset? DischargedAt, AdmissionStatus Status, IReadOnlyList<BedStay> Stays);

/// <summary>One day of the census: movements and the midnight census (patients in a bed at the end of the day).</summary>
public sealed record DailyCensus(DateOnly Date, int Admissions, int Discharges, int Deaths, int TransfersIn, int TransfersOut, int MidnightCensus);

/// <summary>Census statistics for a period (MIS: admissions, discharges, ALOS and bed occupancy rate).</summary>
public sealed record CensusSummary(
    DateOnly From,
    DateOnly To,
    int Admissions,
    int Discharges,
    int Deaths,
    int InpatientDays,
    decimal AverageLengthOfStay,
    decimal BedOccupancyRate,
    IReadOnlyList<DailyCensus> Days);

/// <summary>
/// Census arithmetic (WB-06). Hospital-level figures count admissions and discharges; ward-level figures attribute an
/// admission to the ward of its first bed, a discharge to the ward of its last bed, and count transfers between wards
/// as transfers in/out. The bed occupancy rate is inpatient days (midnight census) ÷ (beds in service × days).
/// </summary>
public static class CensusCalculator
{
    public static CensusSummary Summarize(IReadOnlyCollection<StayRecord> stays, DateOnly from, DateOnly to, int bedsInService, Guid? wardId = null)
    {
        var days = new List<DailyCensus>();
        var inpatientDays = 0;

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var census = IndiaClock.StartOfDayUtc(date.AddDays(1));
            var admissions = stays.Count(s => IndiaClock.LocalDate(s.AdmittedAt) == date && (wardId is null || FirstWard(s) == wardId));
            var discharged = stays.Where(s => s.DischargedAt is { } at && IndiaClock.LocalDate(at) == date && (wardId is null || LastWard(s) == wardId)).ToList();
            var (transfersIn, transfersOut) = wardId is { } ward ? Transfers(stays, ward, date) : (0, 0);
            var midnight = stays.Count(s => s.AdmittedAt < census && (s.DischargedAt is null || s.DischargedAt >= census)
                                            && (wardId is null || BedDayCalculator.StayAt(s.Stays, census)?.WardId == wardId));
            inpatientDays += midnight;
            days.Add(new DailyCensus(date, admissions, discharged.Count, discharged.Count(s => s.Status == AdmissionStatus.Expired), transfersIn, transfersOut, midnight));
        }

        var start = IndiaClock.StartOfDayUtc(from);
        var end = IndiaClock.StartOfDayUtc(to.AddDays(1));
        var dischargedInPeriod = stays
            .Where(s => s.DischargedAt is { } at && at >= start && at < end && (wardId is null || LastWard(s) == wardId))
            .ToList();
        var averageStay = dischargedInPeriod.Count == 0
            ? 0
            : Math.Round((decimal)dischargedInPeriod.Sum(s => LengthOfStay.Days(s.AdmittedAt, s.DischargedAt!.Value)) / dischargedInPeriod.Count, 1, MidpointRounding.AwayFromZero);
        var dayCount = to.DayNumber - from.DayNumber + 1;
        var occupancy = bedsInService <= 0 || dayCount <= 0
            ? 0
            : Math.Round(inpatientDays * 100m / (bedsInService * dayCount), 1, MidpointRounding.AwayFromZero);

        return new CensusSummary(from, to, days.Sum(d => d.Admissions), days.Sum(d => d.Discharges), days.Sum(d => d.Deaths), inpatientDays,
            averageStay, occupancy, days);
    }

    private static Guid? FirstWard(StayRecord stay) => stay.Stays.OrderBy(s => s.From).FirstOrDefault()?.WardId;

    private static Guid? LastWard(StayRecord stay) => stay.Stays.OrderBy(s => s.From).LastOrDefault()?.WardId;

    private static (int In, int Out) Transfers(IReadOnlyCollection<StayRecord> stays, Guid wardId, DateOnly date)
    {
        int transfersIn = 0, transfersOut = 0;
        foreach (var stay in stays)
        {
            var ordered = stay.Stays.OrderBy(s => s.From).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (IndiaClock.LocalDate(ordered[i].From) != date || ordered[i].WardId == ordered[i - 1].WardId)
                {
                    continue;
                }

                if (ordered[i].WardId == wardId)
                {
                    transfersIn++;
                }
                else if (ordered[i - 1].WardId == wardId)
                {
                    transfersOut++;
                }
            }
        }

        return (transfersIn, transfersOut);
    }
}
