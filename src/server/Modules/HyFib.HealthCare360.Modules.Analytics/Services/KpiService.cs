using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>A day's value in a KPI trend (sparkline).</summary>
public sealed record KpiPoint(DateOnly Date, decimal Value);

/// <summary>
/// One executive KPI: this period, the previous period of the same length, the change, which direction is good, where
/// to drill down and how it is calculated.
/// </summary>
public sealed record KpiValue(
    string Key,
    string Label,
    string Unit,
    decimal? Value,
    decimal? Previous,
    decimal? ChangePercent,
    bool HigherIsBetter,
    string? ReportId,
    string Definition,
    IReadOnlyList<KpiPoint>? Trend);

/// <summary>GET /api/v1/analytics/kpis — the executive KPI set (BI-01).</summary>
public sealed record KpiSet(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    string FacilityName,
    IReadOnlyList<KpiValue> Kpis,
    DateTimeOffset GeneratedAt,
    string DataSource);

/// <summary>
/// Executive KPIs (OP visits, admissions, bed occupancy, ALOS, revenue, collections, lab TAT and NPS) for a period and
/// the period before it, computed with the same queries as the catalogue reports they drill into. Finance KPIs are
/// included only for users with <see cref="AnalyticsPermissions.FinanceRead"/>. Results are cached for two minutes per
/// facility, period and audience, so a busy morning of dashboard refreshes costs a handful of queries.
/// </summary>
internal sealed class KpiService(ReportingDbContextFactory contexts, ReportingDatabase reporting, FacilityAccess facilities, IMemoryCache cache, TimeProvider time)
{
    public const int MaxDays = 366;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public async Task<Result<KpiSet>> GetAsync(ReportActor actor, DateOnly? from, DateOnly? to, string? facility, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = AnalyticsClock.LocalDate(now);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (end > today)
        {
            return Error.Field("to", "The end date cannot be in the future.");
        }

        if (start > end)
        {
            return Error.Field("from", "The start date must be on or before the end date.");
        }

        if (AnalyticsClock.Days(start, end) > MaxDays)
        {
            return Error.Field("from", $"Choose a period of at most {MaxDays} days.");
        }

        var operations = actor.HasAny(Audience.Operations);
        var finance = actor.Has(AnalyticsPermissions.FinanceRead);
        if (!operations && !finance)
        {
            return Error.Forbidden("analytics.kpis_forbidden", "You do not have access to hospital KPIs.");
        }

        var scope = await facilities.ResolveAsync(actor, facility, ct);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var key = $"analytics:kpis:{scope.Value.FacilityId?.ToString() ?? ReportKeys.AllFacilities}:{start:yyyyMMdd}:{end:yyyyMMdd}:{operations}:{finance}";
        if (cache.TryGetValue(key, out KpiSet? cached) && cached is not null)
        {
            return cached;
        }

        var (previousFrom, previousTo) = ReportPeriods.Previous(start, end);
        await using var db = contexts.Create(scope.Value.FacilityId);
        var current = await ComputeAsync(db, start, end, now, operations, finance, ct);
        var previous = await ComputeAsync(db, previousFrom, previousTo, now, operations, finance, ct);

        var kpis = new List<KpiValue>();
        if (operations)
        {
            kpis.Add(Kpi(KpiDefinitions.OpVisits, current.OpVisits, previous.OpVisits, Trend(current.OpDaily, start, end)));
            kpis.Add(Kpi(KpiDefinitions.Admissions, current.Admissions, previous.Admissions, Trend(current.AdmissionDaily, start, end)));
            kpis.Add(Kpi(KpiDefinitions.Occupancy, current.Occupancy, previous.Occupancy, null));
            kpis.Add(Kpi(KpiDefinitions.Alos, current.Alos, previous.Alos, null));
            kpis.Add(Kpi(KpiDefinitions.LabTat, current.LabTat, previous.LabTat, null));
            kpis.Add(Kpi(KpiDefinitions.Nps, current.Nps, previous.Nps, null));
        }

        if (finance)
        {
            kpis.Add(Kpi(KpiDefinitions.Revenue, current.Revenue, previous.Revenue, Trend(current.RevenueDaily, start, end)));
            kpis.Add(Kpi(KpiDefinitions.Collections, current.Collections, previous.Collections, Trend(current.CollectionDaily, start, end)));
        }

        var set = new KpiSet(start, end, previousFrom, previousTo, scope.Value.Name, kpis, now, reporting.SourceLabel);
        cache.Set(key, set, CacheDuration);
        return set;
    }

    private static KpiValue Kpi(KpiDefinition definition, decimal? value, decimal? previous, IReadOnlyList<KpiPoint>? trend) =>
        new(definition.Key, definition.Label, definition.Unit, value, previous, KpiMath.ChangePercent(value, previous), definition.HigherIsBetter,
            definition.ReportId, definition.Formula, trend);

    /// <summary>Whether the actor sees a KPI on the dashboard (and so in the KPI catalogue).</summary>
    public static bool CanSee(KpiDefinition definition, ReportActor actor) => definition.Audience switch
    {
        KpiAudience.Finance => actor.Has(AnalyticsPermissions.FinanceRead),
        _ => actor.HasAny(Audience.Operations),
    };

    private static IReadOnlyList<KpiPoint>? Trend(IReadOnlyDictionary<DateOnly, decimal>? daily, DateOnly from, DateOnly to) =>
        daily is null ? null : Enumerable.Range(0, AnalyticsClock.Days(from, to)).Select(i => from.AddDays(i)).Select(d => new KpiPoint(d, daily.GetValueOrDefault(d))).ToList();

    private static async Task<KpiNumbers> ComputeAsync(HospitalDbContext db, DateOnly from, DateOnly to, DateTimeOffset now, bool operations, bool finance, CancellationToken ct)
    {
        var args = new ReportArguments(from, to, ReportPeriods.Custom, null, new Dictionary<string, string>());
        var ctx = new ReportContext(db, args, now, EfReportQueryExecutor.Instance, 10_000);
        var (start, end) = (args.Start, args.End);
        var numbers = new KpiNumbers();

        if (operations)
        {
            var opDaily = await ctx.ListAsync(
                db.Set<Visit>().AsNoTracking()
                    .Where(v => v.VisitedAt >= start && v.VisitedAt < end && v.Type != VisitType.InPatient && v.Status != VisitStatus.Cancelled)
                    .GroupBy(v => DateOnly.FromDateTime(v.VisitedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                    .Select(g => new DayValue(g.Key, g.Count())),
                ct);
            var admissionDaily = await ctx.ListAsync(
                InpatientData.Admissions(db)
                    .Where(a => a.AdmittedAt >= start && a.AdmittedAt < end)
                    .GroupBy(a => DateOnly.FromDateTime(a.AdmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                    .Select(g => new DayValue(g.Key, g.Count())),
                ct);
            var stays = await ctx.MeasurementsAsync(InpatientData.AdmissionStays(db, start, end), ct);
            var beds = await ctx.ListAsync(InpatientData.BedsInService(db), ct);
            var discharges = await ctx.SingleAsync(
                InpatientData.Discharged(db, start, end)
                    .Select(a => DateOnly.FromDateTime(a.DischargedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber
                                 - DateOnly.FromDateTime(a.AdmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber)
                    .GroupBy(stay => 1)
                    .Select(g => new StayTotal(g.Count(), g.Sum(stay => stay < 1 ? 1 : stay))),
                ct);
            var labMinutes = await ctx.MeasurementsAsync(
                from i in db.Set<LabOrderItem>().AsNoTracking()
                join o in db.Set<LabOrder>() on i.LabOrderId equals o.Id
                where o.OrderedAt >= start && o.OrderedAt < end && i.Status == LabOrderItemStatus.Released && i.ReleasedAt != null
                select (i.ReleasedAt!.Value - o.OrderedAt).TotalMinutes,
                ct);
            var nps = await ctx.SingleAsync(
                db.Set<FeedbackResponse>().AsNoTracking()
                    .Where(r => r.SubmittedAt >= start && r.SubmittedAt < end && r.Nps != null)
                    .GroupBy(r => 1)
                    .Select(g => new NpsTotal(g.Count(r => r.Nps >= 9), g.Count(r => r.Nps >= 7 && r.Nps <= 8), g.Count(r => r.Nps <= 6))),
                ct);

            numbers.OpDaily = opDaily.ToDictionary(d => d.Day, d => (decimal)d.Value);
            numbers.OpVisits = opDaily.Sum(d => d.Value);
            numbers.AdmissionDaily = admissionDaily.ToDictionary(d => d.Day, d => (decimal)d.Value);
            numbers.Admissions = admissionDaily.Sum(d => d.Value);
            numbers.Occupancy = KpiMath.OccupancyPercent(InpatientData.CensusDays(stays, from, to), beds.Sum(b => b.Count), AnalyticsClock.Days(from, to));
            numbers.Alos = KpiMath.AverageLengthOfStay(discharges?.StayDays ?? 0, discharges?.Discharges ?? 0);
            numbers.LabTat = Figures.Round1(Percentiles.Summarize(labMinutes).MedianMinutes);
            numbers.Nps = nps is null ? null : KpiMath.NetPromoterScore(nps.Promoters, nps.Passives, nps.Detractors);
        }

        if (finance)
        {
            var billed = await ctx.ListAsync(
                db.Set<Bill>().AsNoTracking()
                    .Where(b => b.FinalizedAt >= start && b.FinalizedAt < end
                                && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid || b.Status == BillStatus.Paid))
                    .GroupBy(b => DateOnly.FromDateTime(b.FinalizedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                    .Select(g => new DayAmount(g.Key, g.Sum(b => b.NetAmount))),
                ct);
            var pharmacy = await ctx.ListAsync(
                db.Set<PharmacySale>().AsNoTracking()
                    .Where(s => s.SoldAt >= start && s.SoldAt < end && s.Status != SaleStatus.Cancelled)
                    .GroupBy(s => DateOnly.FromDateTime(s.SoldAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                    .Select(g => new DayAmount(g.Key, g.Sum(s => s.NetAmount - s.ReturnedAmount))),
                ct);
            var collections = await DailyRevenueReport.CollectionsByDayAsync(ctx, start, end, ct);

            numbers.RevenueDaily = billed.Concat(pharmacy).GroupBy(d => d.Day).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
            numbers.Revenue = numbers.RevenueDaily.Values.Sum();
            numbers.CollectionDaily = collections.GroupBy(d => d.Day).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
            numbers.Collections = numbers.CollectionDaily.Values.Sum();
        }

        return numbers;
    }

    private sealed class KpiNumbers
    {
        public decimal? OpVisits { get; set; }
        public Dictionary<DateOnly, decimal>? OpDaily { get; set; }
        public decimal? Admissions { get; set; }
        public Dictionary<DateOnly, decimal>? AdmissionDaily { get; set; }
        public decimal? Occupancy { get; set; }
        public decimal? Alos { get; set; }
        public decimal? LabTat { get; set; }
        public decimal? Nps { get; set; }
        public decimal? Revenue { get; set; }
        public Dictionary<DateOnly, decimal>? RevenueDaily { get; set; }
        public decimal? Collections { get; set; }
        public Dictionary<DateOnly, decimal>? CollectionDaily { get; set; }
    }

    private sealed record DayValue(DateOnly Day, int Value);

    private sealed record DayAmount(DateOnly Day, decimal Amount);

    private sealed record StayTotal(int Discharges, int StayDays);

    private sealed record NpsTotal(int Promoters, int Passives, int Detractors);
}
