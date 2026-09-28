using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Quality.Features.Dashboard;

public sealed record CountByKey(string Key, int Count);

public sealed record MonthlyIncidents(string Month, int Total, int Sentinel);

public sealed record CapaSummary(int Open, int InProgress, int AwaitingVerification, int Overdue, int VerifiedInPeriod);

public sealed record OverdueCapaItem(Guid Id, string CapaNumber, string Action, string? OwnerName, DateOnly DueDate, int DaysOverdue);

public sealed record TopRiskItem(Guid Id, string RiskNumber, string Title, int Score, RiskRating Rating, int? ResidualScore, DateOnly ReviewDate);

public sealed record OffTargetIndicator(Guid Id, string Code, string Name, string PeriodLabel, decimal Value, decimal? Target, IndicatorDirection Direction);

public sealed record QualityDashboard(
    DateOnly From,
    DateOnly To,
    int TotalIncidents,
    int SentinelEvents,
    int OpenIncidents,
    IReadOnlyList<CountByKey> ByType,
    IReadOnlyList<CountByKey> BySeverity,
    IReadOnlyList<CountByKey> OpenByStatus,
    IReadOnlyList<MonthlyIncidents> ByMonth,
    CapaSummary Capas,
    IReadOnlyList<OverdueCapaItem> OverdueCapas,
    IReadOnlyList<TopRiskItem> TopRisks,
    int RisksDueForReview,
    IReadOnlyList<OffTargetIndicator> IndicatorsOffTarget);

/// <summary>/api/v1/quality/dashboard — the quality committee's one-page view (defaults to the last 12 months).</summary>
internal static class QualityDashboardEndpoints
{
    private const int ListSize = 10;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/quality/dashboard", Get)
            .WithTags("Quality")
            .RequireAnyPermission(QualityPermissions.IncidentsManage, QualityPermissions.CapaManage, QualityPermissions.RisksManage, QualityPermissions.IndicatorsManage);

    private static async Task<IResult> Get(DateOnly? from, DateOnly? to, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = time.LocalToday();
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1).AddMonths(-11);
        if (end < start || end.DayNumber - start.DayNumber > 3 * 366)
        {
            return Error.Field("to", "Choose a range of at most three years.").ToProblem();
        }

        var since = QualityClock.StartOfDayUtc(start);
        var until = QualityClock.StartOfDayUtc(end.AddDays(1));
        var incidents = await db.Set<Incident>().AsNoTracking()
            .Where(i => i.OccurredAt >= since && i.OccurredAt < until)
            .Select(i => new { i.Type, i.Severity, i.IsSentinel, i.OccurredAt })
            .ToListAsync(ct);
        var openByStatus = await db.Set<Incident>().AsNoTracking()
            .Where(i => i.Status != IncidentStatus.Closed)
            .GroupBy(i => i.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var capas = await db.Set<Domain.Capa>().AsNoTracking()
            .Where(c => c.Status != CapaStatus.Cancelled && (c.Status != CapaStatus.Verified || c.VerifiedAt >= since))
            .Select(c => new { c.Id, c.CapaNumber, c.Action, c.OwnerUserId, c.DueDate, c.Status })
            .ToListAsync(ct);
        var overdue = capas.Where(c => c.Status is CapaStatus.Open or CapaStatus.InProgress && c.DueDate < today).OrderBy(c => c.DueDate).ToList();
        var ownerIds = overdue.Take(ListSize).Select(c => c.OwnerUserId).Distinct().ToList();
        var owners = await db.Set<User>().AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var topRisks = await db.Set<Risk>().AsNoTracking()
            .Where(r => r.Status != RiskStatus.Closed)
            .OrderByDescending(r => r.ResidualScore ?? r.Score).ThenByDescending(r => r.Score)
            .Take(ListSize)
            .Select(r => new TopRiskItem(r.Id, r.RiskNumber, r.Title, r.Score, r.Rating, r.ResidualScore, r.ReviewDate))
            .ToListAsync(ct);
        var risksDue = await db.Set<Risk>().CountAsync(r => r.Status != RiskStatus.Closed && r.ReviewDate < today, ct);

        var offTarget = await (
                from v in db.Set<IndicatorValue>().AsNoTracking()
                join i in db.Set<QualityIndicator>() on v.IndicatorId equals i.Id
                where i.IsActive && v.MeetsTarget == false
                      && v.PeriodStart == db.Set<IndicatorValue>().Where(x => x.IndicatorId == v.IndicatorId).Max(x => x.PeriodStart)
                orderby i.SortOrder
                select new { i.Id, i.Code, i.Name, i.Frequency, v.PeriodStart, v.Value, i.Target, i.Direction })
            .ToListAsync(ct);

        var months = new List<MonthlyIncidents>();
        for (var month = new DateOnly(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            var inMonth = incidents.Where(i => QualityClock.LocalMonth(i.OccurredAt) == key).ToList();
            months.Add(new MonthlyIncidents(key, inMonth.Count, inMonth.Count(i => i.IsSentinel)));
        }

        return TypedResults.Ok(new QualityDashboard(
            start,
            end,
            incidents.Count,
            incidents.Count(i => i.IsSentinel),
            openByStatus.Sum(s => s.Count),
            incidents.GroupBy(i => i.Type).OrderByDescending(g => g.Count()).Select(g => new CountByKey(g.Key.ToString(), g.Count())).ToList(),
            incidents.GroupBy(i => i.Severity).OrderBy(g => g.Key).Select(g => new CountByKey(g.Key.ToString(), g.Count())).ToList(),
            openByStatus.OrderBy(s => s.Key).Select(s => new CountByKey(s.Key.ToString(), s.Count)).ToList(),
            months,
            new CapaSummary(
                capas.Count(c => c.Status == CapaStatus.Open),
                capas.Count(c => c.Status == CapaStatus.InProgress),
                capas.Count(c => c.Status == CapaStatus.Completed),
                overdue.Count,
                capas.Count(c => c.Status == CapaStatus.Verified)),
            overdue.Take(ListSize).Select(c => new OverdueCapaItem(c.Id, c.CapaNumber, c.Action, owners.GetValueOrDefault(c.OwnerUserId), c.DueDate,
                today.DayNumber - c.DueDate.DayNumber)).ToList(),
            topRisks,
            risksDue,
            offTarget.Select(o => new OffTargetIndicator(o.Id, o.Code, o.Name, IndicatorMath.PeriodLabel(o.PeriodStart, o.Frequency), o.Value, o.Target, o.Direction)).ToList()));
    }
}
