namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>Timings of one booking as needed for utilisation analysis.</summary>
public sealed record OtCaseTiming(
    Guid TheatreId,
    DateOnly Day,
    SurgeryStatus Status,
    DateTimeOffset ScheduledStart,
    DateTimeOffset ScheduledEnd,
    DateTimeOffset? InRoomAt,
    DateTimeOffset? OutOfRoomAt);

public sealed record TheatreUtilisation(
    Guid TheatreId,
    int CasesCompleted,
    int CasesCancelled,
    int CasesPostponed,
    int BookedMinutes,
    int ActualMinutes,
    double? ActualToBookedPercent,
    int AvailableMinutes,
    double? UtilisationPercent,
    int FirstCases,
    int FirstCasesOnTime,
    double? FirstCaseOnTimePercent,
    double? AverageTurnoverMinutes);

/// <summary>
/// OT utilisation (OT-07): booked vs actual minutes, first-case on-time starts and turnover time — the three
/// numbers OT committees in Indian hospitals (and NABH) review monthly.
/// </summary>
public static class OtUtilisation
{
    /// <summary>A first case is "on time" if the patient is in the room within 15 minutes of the scheduled start.</summary>
    public static readonly TimeSpan FirstCaseGrace = TimeSpan.FromMinutes(15);

    /// <summary>Gaps longer than this between consecutive cases are idle time, not turnover, and are excluded.</summary>
    public static readonly TimeSpan MaxTurnover = TimeSpan.FromMinutes(120);

    public static TheatreUtilisation Calculate(Guid theatreId, IEnumerable<OtCaseTiming> cases, int availableMinutes)
    {
        var mine = cases.Where(c => c.TheatreId == theatreId).ToList();
        var completed = mine.Where(c => c.Status == SurgeryStatus.Completed && c.InRoomAt is not null && c.OutOfRoomAt is not null).ToList();

        var booked = completed.Sum(c => (int)Math.Round((c.ScheduledEnd - c.ScheduledStart).TotalMinutes));
        var actual = completed.Sum(c => (int)Math.Round((c.OutOfRoomAt!.Value - c.InRoomAt!.Value).TotalMinutes));

        var firstCases = completed.GroupBy(c => c.Day).Select(g => g.OrderBy(c => c.ScheduledStart).First()).ToList();
        var onTime = firstCases.Count(c => c.InRoomAt!.Value <= c.ScheduledStart + FirstCaseGrace);

        var turnovers = new List<double>();
        foreach (var day in completed.GroupBy(c => c.Day))
        {
            var ordered = day.OrderBy(c => c.InRoomAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var gap = ordered[i].InRoomAt!.Value - ordered[i - 1].OutOfRoomAt!.Value;
                if (gap > TimeSpan.Zero && gap <= MaxTurnover)
                {
                    turnovers.Add(gap.TotalMinutes);
                }
            }
        }

        return new TheatreUtilisation(
            theatreId,
            completed.Count,
            mine.Count(c => c.Status == SurgeryStatus.Cancelled),
            mine.Count(c => c.Status == SurgeryStatus.Postponed),
            booked,
            actual,
            Percent(actual, booked),
            availableMinutes,
            Percent(actual, availableMinutes),
            firstCases.Count,
            onTime,
            Percent(onTime, firstCases.Count),
            turnovers.Count == 0 ? null : Math.Round(turnovers.Average(), 1));
    }

    private static double? Percent(double part, double whole) => whole <= 0 ? null : Math.Round(part * 100 / whole, 1);
}
