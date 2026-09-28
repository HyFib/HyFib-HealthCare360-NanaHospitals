using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>IST day numbers of one bed stay or admission (end null while the patient is still in).</summary>
internal sealed record StaySpan(Guid? Key, int StartDay, int? EndDay);

internal sealed record WardInfo(Guid Id, string Name, WardType Type, int DisplayOrder, bool IsActive);

internal sealed record KeyCount(Guid? Key, int Count);

/// <summary>
/// In-patient building blocks shared by the census, occupancy, turnover and KPI queries. Everything is projected to IST
/// day numbers in SQL; census days are then counted with <see cref="CensusMath"/>, exactly as the Inpatient module's
/// census does.
/// </summary>
internal static class InpatientData
{
    public const string BedsNote = "Beds in service are today's active beds that are not blocked or under maintenance (as in the in-patient census).";

    public static IQueryable<Admission> Admissions(HospitalDbContext db) =>
        db.Set<Admission>().AsNoTracking().Where(a => a.Status != AdmissionStatus.Cancelled);

    /// <summary>Admissions discharged (any outcome) in the period.</summary>
    public static IQueryable<Admission> Discharged(HospitalDbContext db, DateTimeOffset start, DateTimeOffset end) =>
        Admissions(db).Where(a => a.DischargedAt >= start && a.DischargedAt < end);

    /// <summary>Beds in service per ward right now.</summary>
    public static IQueryable<KeyCount> BedsInService(HospitalDbContext db) =>
        db.Set<Bed>().AsNoTracking()
            .Where(b => b.IsActive && b.Status != BedStatus.Blocked && b.Status != BedStatus.Maintenance)
            .GroupBy(b => b.WardId)
            .Select(g => new KeyCount(g.Key, g.Count()));

    /// <summary>Bed stays (keyed by ward) overlapping the period, for ward-level inpatient days.</summary>
    public static IQueryable<StaySpan> WardStays(HospitalDbContext db, DateTimeOffset start, DateTimeOffset end, Guid? wardId = null) =>
        from s in db.Set<BedAssignment>().AsNoTracking()
        join a in db.Set<Admission>() on s.AdmissionId equals a.Id
        where a.Status != AdmissionStatus.Cancelled && s.FromAt < end && (s.ToAt == null || s.ToAt >= start) && (wardId == null || s.WardId == wardId)
        select new StaySpan(
            s.WardId,
            DateOnly.FromDateTime(s.FromAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber,
            s.ToAt == null ? (int?)null : DateOnly.FromDateTime(s.ToAt.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber);

    /// <summary>Admissions (keyed by department) overlapping the period, for hospital-level inpatient days.</summary>
    public static IQueryable<StaySpan> AdmissionStays(HospitalDbContext db, DateTimeOffset start, DateTimeOffset end, Guid? departmentId = null) =>
        Admissions(db)
            .Where(a => a.AdmittedAt < end && (a.DischargedAt == null || a.DischargedAt >= start) && (departmentId == null || a.DepartmentId == departmentId))
            .Select(a => new StaySpan(
                a.DepartmentId,
                DateOnly.FromDateTime(a.AdmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber,
                a.DischargedAt == null ? (int?)null : DateOnly.FromDateTime(a.DischargedAt.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber));

    public static IQueryable<WardInfo> Wards(HospitalDbContext db) =>
        db.Set<Ward>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Select(w => new WardInfo(w.Id, w.Name, w.Type, w.DisplayOrder, w.IsActive && !w.IsDeleted));

    public static int CensusDays(IEnumerable<StaySpan> stays, DateOnly from, DateOnly to) =>
        stays.Sum(s => CensusMath.CensusDays(s.StartDay, s.EndDay, from.DayNumber, to.DayNumber));

    /// <summary>Midnight census for each day of the period (difference array: linear in stays + days).</summary>
    public static int[] DailyCensus(IEnumerable<StaySpan> stays, DateOnly from, DateOnly to)
    {
        var days = AnalyticsClock.Days(from, to);
        var delta = new int[days + 1];
        foreach (var stay in stays)
        {
            var first = Math.Max(from.DayNumber, stay.StartDay);
            var last = stay.EndDay is { } end ? Math.Min(to.DayNumber, end - 1) : to.DayNumber;
            if (last < first)
            {
                continue;
            }

            delta[first - from.DayNumber]++;
            delta[last - from.DayNumber + 1]--;
        }

        var census = new int[days];
        var running = 0;
        for (var i = 0; i < days; i++)
        {
            running += delta[i];
            census[i] = running;
        }

        return census;
    }
}

/// <summary>MIS-01: admissions, discharges, deaths and the midnight census over time.</summary>
internal sealed class AdmissionsDischargesReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "ip-admissions-discharges",
        "Admissions and discharges",
        ReportCategory.InPatient,
        "In-patient admissions, discharges (all outcomes), deaths and the midnight census by day, week or month.",
        "Admissions by IST admission date and discharges by IST discharge date (cancelled admissions excluded). Midnight census = patients in a bed at the midnight ending each day; weekly and monthly rows show the average daily census.",
        Audience.Operations,
        [P.Period(ReportPeriods.Last30Days), P.Facility(), P.Interval(), P.Department()],
        [C.Date("period", "Date"), C.Int("admissions", "Admissions"), C.Int("discharges", "Discharges"), C.Int("deaths", "Deaths"), C.Dec("census", "Midnight census")],
        new ReportChartHint(ReportChartType.Line, "period", ["admissions", "discharges", "census"], TimeSeries: true));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var admissions = InpatientData.Admissions(ctx.Db);
        if (ctx.Args.DepartmentId is { } departmentId)
        {
            admissions = admissions.Where(a => a.DepartmentId == departmentId);
        }

        var admitted = await ctx.ListAsync(
            admissions.Where(a => a.AdmittedAt >= start && a.AdmittedAt < end)
                .GroupBy(a => DateOnly.FromDateTime(a.AdmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                .Select(g => new DayTally(g.Key, g.Count(), 0)),
            ct);
        var discharged = await ctx.ListAsync(
            admissions.Where(a => a.DischargedAt >= start && a.DischargedAt < end)
                .GroupBy(a => DateOnly.FromDateTime(a.DischargedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                .Select(g => new DayTally(g.Key, g.Count(), g.Count(a => a.DischargeType == DischargeType.Expired))),
            ct);
        var stays = await ctx.MeasurementsAsync(
            admissions.Where(a => a.AdmittedAt < end && (a.DischargedAt == null || a.DischargedAt >= start))
                .Select(a => new StaySpan(
                    null,
                    DateOnly.FromDateTime(a.AdmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber,
                    a.DischargedAt == null ? (int?)null : DateOnly.FromDateTime(a.DischargedAt.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber)),
            ct);

        var interval = Trend.Of(ctx.Args);
        var census = InpatientData.DailyCensus(stays, ctx.Args.From, ctx.Args.To);
        var admittedByDay = admitted.ToDictionary(d => d.Day, d => d.Count);
        var dischargedByDay = discharged.ToDictionary(d => d.Day);

        var rows = new List<object?[]>();
        foreach (var bucket in Trend.Buckets(ctx.Args.From, ctx.Args.To, interval))
        {
            int bucketAdmissions = 0, bucketDischarges = 0, bucketDeaths = 0, censusSum = 0, days = 0;
            for (var day = bucket < ctx.Args.From ? ctx.Args.From : bucket; day <= ctx.Args.To && Trend.BucketOf(day, interval) == bucket; day = day.AddDays(1))
            {
                bucketAdmissions += admittedByDay.GetValueOrDefault(day);
                if (dischargedByDay.TryGetValue(day, out var d))
                {
                    bucketDischarges += d.Count;
                    bucketDeaths += d.Deaths;
                }

                censusSum += census[day.DayNumber - ctx.Args.From.DayNumber];
                days++;
            }

            rows.Add([bucket, bucketAdmissions, bucketDischarges, bucketDeaths, Figures.PerDay(censusSum, days)]);
        }

        var totalAdmissions = admitted.Sum(d => d.Count);
        var totalDischarges = discharged.Sum(d => d.Count);
        var totalDeaths = discharged.Sum(d => d.Deaths);
        var averageCensus = Figures.PerDay(census.Sum(), census.Length);
        return new ReportData(rows)
        {
            Columns =
            [
                new ReportColumn("period", Trend.ColumnLabel(interval), interval == Trend.Month ? ReportValueType.Month : ReportValueType.Date),
                .. Descriptor.Columns.Skip(1),
            ],
            Totals = ["Total", totalAdmissions, totalDischarges, totalDeaths, averageCensus],
            Summary =
            [
                new("admissions", "Admissions", totalAdmissions, ReportValueType.Integer),
                new("discharges", "Discharges", totalDischarges, ReportValueType.Integer),
                new("deaths", "Deaths", totalDeaths, ReportValueType.Integer),
                new("census", "Average midnight census", averageCensus, ReportValueType.Decimal),
            ],
        };
    }

    private sealed record DayTally(DateOnly Day, int Count, int Deaths);
}

/// <summary>MIS-01 / WB-06: bed occupancy % by ward.</summary>
internal sealed class BedOccupancyReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "bed-occupancy",
        "Bed occupancy by ward",
        ReportCategory.InPatient,
        "Inpatient days against available bed-days for each ward, with the average daily census.",
        "Inpatient days = patients in a bed of the ward at each IST midnight in the period (bed transfers counted in the ward the patient was in). Available bed-days = beds in service × days. Occupancy % = inpatient days ÷ available bed-days × 100.",
        Audience.Operations,
        [P.Period(), P.Facility(), P.Ward()],
        [
            C.Text("ward", "Ward"),
            C.Text("wardType", "Class"),
            C.Int("beds", "Beds in service"),
            C.Int("inpatientDays", "Inpatient days"),
            C.Int("bedDays", "Available bed-days"),
            C.Pct("occupancy", "Occupancy %"),
            C.Dec("averageCensus", "Average daily census"),
        ],
        new ReportChartHint(ReportChartType.Bar, "ward", ["occupancy"]),
        DefaultSort: "occupancy",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var wardId = ctx.Args.WardId;
        var stays = await ctx.MeasurementsAsync(InpatientData.WardStays(ctx.Db, start, end, wardId), ct);
        var beds = (await ctx.ListAsync(InpatientData.BedsInService(ctx.Db), ct)).ToDictionary(b => b.Key!.Value, b => b.Count);
        var wards = await ctx.ListAsync(InpatientData.Wards(ctx.Db), ct);
        var days = ctx.Args.DayCount;
        var daysByWard = stays.GroupBy(s => s.Key!.Value).ToDictionary(g => g.Key, g => InpatientData.CensusDays(g, ctx.Args.From, ctx.Args.To));

        var rows = wards
            .Where(w => (wardId == null || w.Id == wardId) && (w.IsActive || daysByWard.ContainsKey(w.Id)))
            .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .Select(w =>
            {
                var bedCount = beds.GetValueOrDefault(w.Id);
                var inpatientDays = daysByWard.GetValueOrDefault(w.Id);
                return new object?[]
                {
                    w.Name, Labels.WardType(w.Type), bedCount, inpatientDays, bedCount * days, KpiMath.OccupancyPercent(inpatientDays, bedCount, days),
                    Figures.PerDay(inpatientDays, days),
                };
            })
            .ToList();

        var totalBeds = rows.Sum(r => (int)r[2]!);
        var totalDays = rows.Sum(r => (int)r[3]!);
        var occupancy = KpiMath.OccupancyPercent(totalDays, totalBeds, days);
        return new ReportData(rows)
        {
            Totals = ["Total", null, totalBeds, totalDays, totalBeds * days, occupancy, Figures.PerDay(totalDays, days)],
            Summary =
            [
                new("occupancy", "Bed occupancy", occupancy, ReportValueType.Percent),
                new("inpatientDays", "Inpatient days", totalDays, ReportValueType.Integer),
                new("beds", "Beds in service", totalBeds, ReportValueType.Integer),
                new("averageCensus", "Average daily census", Figures.PerDay(totalDays, days), ReportValueType.Decimal),
            ],
            Notes = [InpatientData.BedsNote],
        };
    }
}

/// <summary>MIS: bed turnover rate and interval by ward class.</summary>
internal sealed class BedTurnoverReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "bed-turnover",
        "Bed turnover by ward class",
        ReportCategory.InPatient,
        "How many patients each bed served and how long beds stood empty between patients, by ward class.",
        "Discharges = stays ended in the period (all outcomes), counted in the patient's last ward. Bed turnover rate = discharges ÷ beds in service. Turnover interval = (available bed-days − inpatient days) ÷ discharges. ALOS = total length of stay of those discharges ÷ discharges.",
        Audience.Operations,
        [P.Period(), P.Facility()],
        [
            C.Text("wardClass", "Ward class"),
            C.Int("beds", "Beds in service"),
            C.Int("discharges", "Discharges"),
            C.Dec("turnoverRate", "Bed turnover rate"),
            C.Days("turnoverInterval", "Turnover interval (days)"),
            C.Pct("occupancy", "Occupancy %"),
            C.Days("alos", "ALOS (days)"),
        ],
        new ReportChartHint(ReportChartType.Bar, "wardClass", ["turnoverRate"]),
        DefaultSort: "discharges",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var days = ctx.Args.DayCount;
        var wards = (await ctx.ListAsync(InpatientData.Wards(ctx.Db), ct)).ToDictionary(w => w.Id);
        var beds = await ctx.ListAsync(InpatientData.BedsInService(ctx.Db), ct);
        var stays = await ctx.MeasurementsAsync(InpatientData.WardStays(ctx.Db, start, end), ct);
        var discharges = await ctx.ListAsync(
            InpatientData.Discharged(ctx.Db, start, end)
                .Select(a => new
                {
                    a.WardId,
                    Stay = DateOnly.FromDateTime(a.DischargedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber
                           - DateOnly.FromDateTime(a.AdmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber,
                })
                .GroupBy(a => a.WardId)
                .Select(g => new WardDischarges(g.Key, g.Count(), g.Sum(a => a.Stay < 1 ? 1 : a.Stay))),
            ct);

        WardType? ClassOf(Guid? wardId) => wardId is { } id && wards.TryGetValue(id, out var ward) ? ward.Type : null;

        var classes = Enum.GetValues<WardType>().Cast<WardType?>().Append(null).ToList();
        var rows = new List<object?[]>();
        foreach (var wardClass in classes)
        {
            var classBeds = beds.Where(b => ClassOf(b.Key) == wardClass).Sum(b => b.Count);
            var classStays = stays.Where(s => ClassOf(s.Key) == wardClass).ToList();
            var classDischarges = discharges.Where(d => ClassOf(d.WardId) == wardClass).ToList();
            var count = classDischarges.Sum(d => d.Count);
            if (classBeds == 0 && count == 0 && classStays.Count == 0)
            {
                continue;
            }

            var inpatientDays = InpatientData.CensusDays(classStays, ctx.Args.From, ctx.Args.To);
            rows.Add(
            [
                wardClass is { } type ? Labels.WardType(type) : "Ward not recorded", classBeds, count, KpiMath.BedTurnoverRate(count, classBeds),
                KpiMath.BedTurnoverInterval(classBeds, days, inpatientDays, count), KpiMath.OccupancyPercent(inpatientDays, classBeds, days),
                KpiMath.AverageLengthOfStay(classDischarges.Sum(d => (long)d.StayDays), count),
            ]);
        }

        var totalBeds = beds.Sum(b => b.Count);
        var totalDischarges = discharges.Sum(d => d.Count);
        var totalDays = InpatientData.CensusDays(stays, ctx.Args.From, ctx.Args.To);
        var alos = KpiMath.AverageLengthOfStay(discharges.Sum(d => (long)d.StayDays), totalDischarges);
        return new ReportData(rows)
        {
            Totals =
            [
                "All wards", totalBeds, totalDischarges, KpiMath.BedTurnoverRate(totalDischarges, totalBeds),
                KpiMath.BedTurnoverInterval(totalBeds, days, totalDays, totalDischarges), KpiMath.OccupancyPercent(totalDays, totalBeds, days), alos,
            ],
            Summary =
            [
                new("turnoverRate", "Bed turnover rate", KpiMath.BedTurnoverRate(totalDischarges, totalBeds), ReportValueType.Decimal),
                new("turnoverInterval", "Turnover interval", KpiMath.BedTurnoverInterval(totalBeds, days, totalDays, totalDischarges), ReportValueType.Days),
                new("discharges", "Discharges", totalDischarges, ReportValueType.Integer),
                new("alos", "ALOS", alos, ReportValueType.Days),
            ],
            Notes = [InpatientData.BedsNote],
        };
    }

    private sealed record WardDischarges(Guid? WardId, int Count, int StayDays);
}

/// <summary>MIS-01: average length of stay by department or consultant.</summary>
internal sealed class AlosReport : ReportDefinition
{
    private const string ByDepartment = "department";
    private const string ByDoctor = "doctor";

    public override ReportDescriptor Descriptor { get; } = new(
        "alos",
        "Average length of stay (ALOS)",
        ReportCategory.InPatient,
        "Average length of stay of patients discharged in the period, by department or consultant.",
        "Length of stay = IST calendar days from admission to discharge, at least one day. ALOS = total length of stay ÷ discharges (all outcomes, cancelled admissions excluded).",
        Audience.Operations,
        [
            P.Period(),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByDepartment, "Department"), new(ByDoctor, "Consultant")], ByDepartment),
            P.Department(),
            P.Doctor("Consultant"),
        ],
        [
            C.Text("department", "Department"),
            C.Text("doctor", "Consultant"),
            C.Int("discharges", "Discharges"),
            C.Int("stayDays", "Total stay (days)"),
            C.Days("alos", "ALOS (days)"),
            C.Int("longest", "Longest stay (days)"),
        ],
        new ReportChartHint(ReportChartType.Bar, "department", ["alos"]),
        DefaultSort: "discharges",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var byDoctor = ctx.Args.Text(ReportKeys.GroupBy) == ByDoctor;
        var admissions = InpatientData.Discharged(ctx.Db, ctx.Args.Start, ctx.Args.End);
        if (ctx.Args.DepartmentId is { } departmentId)
        {
            admissions = admissions.Where(a => a.DepartmentId == departmentId);
        }

        if (ctx.Args.PractitionerId is { } practitionerId)
        {
            admissions = admissions.Where(a => a.ConsultantPractitionerId == practitionerId);
        }

        var stays = admissions.Select(a => new
        {
            a.DepartmentId,
            DoctorId = byDoctor ? a.ConsultantPractitionerId : (Guid?)null,
            Stay = DateOnly.FromDateTime(a.DischargedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber
                   - DateOnly.FromDateTime(a.AdmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber,
        });
        var groups = await ctx.ListAsync(
            stays.GroupBy(s => new { s.DepartmentId, s.DoctorId })
                .Select(g => new AlosRow(g.Key.DepartmentId, g.Key.DoctorId, g.Count(), g.Sum(s => s.Stay < 1 ? 1 : s.Stay), g.Max(s => s.Stay < 1 ? 1 : s.Stay))),
            ct);

        var departments = await Names.DepartmentsAsync(ctx, groups.Select(g => g.DepartmentId), ct);
        var doctors = await Names.PractitionersAsync(ctx, groups.Select(g => g.DoctorId), ct);
        var rows = groups
            .Select(g => new object?[]
            {
                Labels.Name(departments, g.DepartmentId), byDoctor ? Labels.Name(doctors, g.DoctorId, "No consultant recorded") : "All consultants",
                g.Discharges, g.StayDays, KpiMath.AverageLengthOfStay(g.StayDays, g.Discharges), g.Longest,
            })
            .ToList();

        var discharges = groups.Sum(g => g.Discharges);
        var totalStay = groups.Sum(g => (long)g.StayDays);
        var alos = KpiMath.AverageLengthOfStay(totalStay, discharges);
        return new ReportData(rows)
        {
            Totals = ["Total", null, discharges, totalStay, alos, groups.Count == 0 ? null : groups.Max(g => g.Longest)],
            Summary =
            [
                new("alos", "ALOS", alos, ReportValueType.Days),
                new("discharges", "Discharges", discharges, ReportValueType.Integer),
                new("stayDays", "Total stay (days)", totalStay, ReportValueType.Integer),
            ],
            Chart = byDoctor ? new ReportChartHint(ReportChartType.Bar, "doctor", ["alos"]) : null,
        };
    }

    private sealed record AlosRow(Guid? DepartmentId, Guid? DoctorId, int Discharges, int StayDays, int Longest);
}

/// <summary>WB-02: live census by ward (bed board summary).</summary>
internal sealed class WardCensusReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "ward-census",
        "Census by ward (now)",
        ReportCategory.InPatient,
        "Beds by status in every ward right now, with patients whose discharge is in progress.",
        "Counts active beds by their current status. Occupancy now = occupied ÷ beds in service (blocked and maintenance beds are out of service).",
        Audience.Operations,
        [P.Facility(), P.Ward()],
        [
            C.Text("ward", "Ward"),
            C.Text("wardType", "Class"),
            C.Int("total", "Beds"),
            C.Int("occupied", "Occupied"),
            C.Int("vacant", "Vacant"),
            C.Int("reserved", "Reserved"),
            C.Int("cleaning", "Cleaning"),
            C.Int("outOfService", "Blocked / maintenance"),
            C.Pct("occupancy", "Occupancy now %"),
            C.Int("dischargeInProgress", "Discharge in progress"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "ward", ["occupied", "vacant", "reserved", "cleaning", "outOfService"]));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var wardId = ctx.Args.WardId;
        var beds = await ctx.ListAsync(
            ctx.Db.Set<Bed>().AsNoTracking()
                .Where(b => b.IsActive && (wardId == null || b.WardId == wardId))
                .GroupBy(b => b.WardId)
                .Select(g => new BedRow(
                    g.Key,
                    g.Count(),
                    g.Count(b => b.Status == BedStatus.Occupied),
                    g.Count(b => b.Status == BedStatus.Vacant),
                    g.Count(b => b.Status == BedStatus.Reserved),
                    g.Count(b => b.Status == BedStatus.Cleaning),
                    g.Count(b => b.Status == BedStatus.Blocked || b.Status == BedStatus.Maintenance))),
            ct);
        var discharging = (await ctx.ListAsync(
                InpatientData.Admissions(ctx.Db)
                    .Where(a => a.Status == AdmissionStatus.DischargeInitiated && (wardId == null || a.WardId == wardId))
                    .GroupBy(a => a.WardId)
                    .Select(g => new KeyCount(g.Key, g.Count())),
                ct))
            .Where(k => k.Key is not null)
            .ToDictionary(k => k.Key!.Value, k => k.Count);
        var wards = (await ctx.ListAsync(InpatientData.Wards(ctx.Db), ct)).ToDictionary(w => w.Id);

        var rows = beds
            .OrderBy(b => wards.TryGetValue(b.WardId, out var w) ? w.DisplayOrder : int.MaxValue)
            .ThenBy(b => wards.TryGetValue(b.WardId, out var w) ? w.Name : string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(b => new object?[]
            {
                wards.TryGetValue(b.WardId, out var w) ? w.Name : "Unknown ward", w is null ? null : Labels.WardType(w.Type), b.Total, b.Occupied, b.Vacant,
                b.Reserved, b.Cleaning, b.OutOfService, KpiMath.Percent(b.Occupied, b.Total - b.OutOfService), discharging.GetValueOrDefault(b.WardId),
            })
            .ToList();

        var total = beds.Sum(b => b.Total);
        var occupied = beds.Sum(b => b.Occupied);
        var outOfService = beds.Sum(b => b.OutOfService);
        return new ReportData(rows)
        {
            Totals =
            [
                "Total", null, total, occupied, beds.Sum(b => b.Vacant), beds.Sum(b => b.Reserved), beds.Sum(b => b.Cleaning), outOfService,
                KpiMath.Percent(occupied, total - outOfService), discharging.Values.Sum(),
            ],
            Summary =
            [
                new("occupied", "Occupied beds", occupied, ReportValueType.Integer),
                new("vacant", "Vacant beds", beds.Sum(b => b.Vacant), ReportValueType.Integer),
                new("occupancy", "Occupancy now", KpiMath.Percent(occupied, total - outOfService), ReportValueType.Percent),
                new("dischargeInProgress", "Discharges in progress", discharging.Values.Sum(), ReportValueType.Integer),
            ],
        };
    }

    private sealed record BedRow(Guid WardId, int Total, int Occupied, int Vacant, int Reserved, int Cleaning, int OutOfService);
}

/// <summary>MIS-01 / NABH: discharge outcomes, LAMA/DAMA rates and gross/net mortality.</summary>
internal sealed class DischargeOutcomesReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "discharge-outcomes",
        "Discharge outcomes, LAMA/DAMA and mortality",
        ReportCategory.InPatient,
        "How in-patient stays ended by department: normal, referred, LAMA, DAMA, absconded and deaths, with mortality rates.",
        "Discharges = all stays ended in the period (including deaths). LAMA % and DAMA % = those discharges ÷ discharges. Gross death rate = deaths ÷ discharges × 100. Net death rate = (deaths − deaths within 48 hours of admission) ÷ (discharges − deaths within 48 hours) × 100.",
        Audience.Operations,
        [P.Period(), P.Facility(), P.Department()],
        [
            C.Text("department", "Department"),
            C.Int("discharges", "Discharges"),
            C.Int("normal", "Normal"),
            C.Int("referred", "Referred out"),
            C.Int("lama", "LAMA"),
            C.Int("dama", "DAMA"),
            C.Int("absconded", "Absconded"),
            C.Int("deaths", "Deaths"),
            C.Int("deaths48", "Deaths within 48 h"),
            C.Pct("lamaPercent", "LAMA %"),
            C.Pct("damaPercent", "DAMA %"),
            C.Pct("grossDeathRate", "Gross death rate %"),
            C.Pct("netDeathRate", "Net death rate %"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "department", ["normal", "referred", "lama", "dama", "absconded", "deaths"]),
        DefaultSort: "discharges",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var admissions = InpatientData.Discharged(ctx.Db, ctx.Args.Start, ctx.Args.End);
        if (ctx.Args.DepartmentId is { } departmentId)
        {
            admissions = admissions.Where(a => a.DepartmentId == departmentId);
        }

        var groups = await ctx.ListAsync(
            admissions.GroupBy(a => a.DepartmentId)
                .Select(g => new OutcomeRow(
                    g.Key,
                    g.Count(),
                    g.Count(a => a.DischargeType == DischargeType.Normal || a.DischargeType == null),
                    g.Count(a => a.DischargeType == DischargeType.Referred),
                    g.Count(a => a.DischargeType == DischargeType.Lama),
                    g.Count(a => a.DischargeType == DischargeType.Dama),
                    g.Count(a => a.DischargeType == DischargeType.Absconded),
                    g.Count(a => a.DischargeType == DischargeType.Expired),
                    g.Count(a => a.DischargeType == DischargeType.Expired && a.DischargedAt < a.AdmittedAt.AddHours(48)))),
            ct);

        var departments = await Names.DepartmentsAsync(ctx, groups.Select(g => g.DepartmentId), ct);
        var rows = groups.Select(g => Row(Labels.Name(departments, g.DepartmentId), g)).ToList();
        var total = new OutcomeRow(
            null, groups.Sum(g => g.Discharges), groups.Sum(g => g.Normal), groups.Sum(g => g.Referred), groups.Sum(g => g.Lama), groups.Sum(g => g.Dama),
            groups.Sum(g => g.Absconded), groups.Sum(g => g.Deaths), groups.Sum(g => g.Deaths48));
        return new ReportData(rows)
        {
            Totals = Row("Total", total),
            Summary =
            [
                new("discharges", "Discharges", total.Discharges, ReportValueType.Integer),
                new("lamaPercent", "LAMA rate", KpiMath.Percent(total.Lama, total.Discharges, 2), ReportValueType.Percent),
                new("damaPercent", "DAMA rate", KpiMath.Percent(total.Dama, total.Discharges, 2), ReportValueType.Percent),
                new("grossDeathRate", "Gross death rate", KpiMath.GrossDeathRate(total.Deaths, total.Discharges), ReportValueType.Percent),
                new("netDeathRate", "Net death rate", KpiMath.NetDeathRate(total.Deaths, total.Deaths48, total.Discharges), ReportValueType.Percent),
            ],
        };
    }

    private static object?[] Row(string label, OutcomeRow g) =>
    [
        label, g.Discharges, g.Normal, g.Referred, g.Lama, g.Dama, g.Absconded, g.Deaths, g.Deaths48, KpiMath.Percent(g.Lama, g.Discharges, 2),
        KpiMath.Percent(g.Dama, g.Discharges, 2), KpiMath.GrossDeathRate(g.Deaths, g.Discharges), KpiMath.NetDeathRate(g.Deaths, g.Deaths48, g.Discharges),
    ];

    private sealed record OutcomeRow(Guid? DepartmentId, int Discharges, int Normal, int Referred, int Lama, int Dama, int Absconded, int Deaths, int Deaths48);
}

/// <summary>
/// Mortality and LAMA/DAMA review register (patient-level; requires analytics.clinical.read, every view and export is
/// audited per patient). Used by mortality review and MRD committees.
/// </summary>
internal sealed class AdverseDischargesReport : ReportDefinition
{
    private static readonly DischargeType[] Adverse = [DischargeType.Expired, DischargeType.Lama, DischargeType.Dama, DischargeType.Absconded, DischargeType.Referred];

    public override ReportDescriptor Descriptor { get; } = new(
        "adverse-discharges",
        "Deaths, LAMA and DAMA register",
        ReportCategory.InPatient,
        "Patient-wise list of deaths, LAMA, DAMA, absconded and referred-out discharges for mortality and MRD review.",
        "Stays ended in the period with one of the chosen outcomes. Length of stay in IST calendar days (at least one). Within 48 h = death within 48 hours of admission. Lists patient identifiers: opening or exporting it is recorded in the audit trail for each patient.",
        Audience.Clinical,
        [
            P.Period(),
            P.Facility(),
            P.Department(),
            P.Choice(ReportKeys.Outcome, "Outcome", [new("all", "All of these outcomes"), .. Choices.Of(Adverse, Labels.DischargeType)], "all"),
        ],
        [
            C.Text("admissionNumber", "Admission no."),
            C.Patient("uhid", "UHID"),
            C.Patient("patient", "Patient"),
            C.Text("ageSex", "Age / sex"),
            C.Text("department", "Department"),
            C.Text("consultant", "Consultant"),
            C.DateTime("admittedAt", "Admitted"),
            C.DateTime("dischargedAt", "Discharged"),
            C.Days("los", "Stay (days)"),
            C.Text("outcome", "Outcome"),
            C.Text("within48", "Within 48 h"),
            C.Text("mlc", "MLC"),
        ],
        null,
        DefaultSort: "dischargedAt",
        DefaultSortDescending: true,
        ContainsPatientIdentifiers: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var outcome = ctx.Args.EnumOf<DischargeType>(ReportKeys.Outcome);
        var admissions = InpatientData.Discharged(ctx.Db, ctx.Args.Start, ctx.Args.End)
            .Where(a => a.DischargeType == DischargeType.Expired || a.DischargeType == DischargeType.Lama || a.DischargeType == DischargeType.Dama
                        || a.DischargeType == DischargeType.Absconded || a.DischargeType == DischargeType.Referred);
        if (outcome is { } chosen)
        {
            admissions = admissions.Where(a => a.DischargeType == chosen);
        }

        if (ctx.Args.DepartmentId is { } departmentId)
        {
            admissions = admissions.Where(a => a.DepartmentId == departmentId);
        }

        var list = await ctx.ListAsync(
            from a in admissions
            join p in ctx.Db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on a.PatientId equals p.Id
            orderby a.DischargedAt descending
            select new RegisterRow(
                a.PatientId, a.AdmissionNumber, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, a.DepartmentId, a.ConsultantPractitionerId, a.AdmittedAt,
                a.DischargedAt!.Value, a.DischargeType, a.IsMlc),
            ct);

        var departments = await Names.DepartmentsAsync(ctx, list.Select(r => r.DepartmentId), ct);
        var doctors = await Names.PractitionersAsync(ctx, list.Select(r => r.ConsultantId), ct);
        var rows = list
            .Select(r =>
            {
                var admitted = AnalyticsClock.LocalDate(r.AdmittedAt);
                var discharged = AnalyticsClock.LocalDate(r.DischargedAt);
                var age = r.DateOfBirth is { } dob ? Age.Format(dob, discharged) : "—";
                var within48 = r.Outcome == DischargeType.Expired && r.DischargedAt - r.AdmittedAt < TimeSpan.FromHours(48);
                return new object?[]
                {
                    r.AdmissionNumber, r.Uhid, r.PatientName, $"{age} / {r.Gender.ToString()[..1]}", Labels.Name(departments, r.DepartmentId),
                    Labels.Name(doctors, r.ConsultantId), r.AdmittedAt, r.DischargedAt, KpiMath.LengthOfStayDays(admitted, discharged),
                    r.Outcome is { } type ? Labels.DischargeType(type) : "—", r.Outcome == DischargeType.Expired ? (within48 ? "Yes" : "No") : "—", r.IsMlc ? "Yes" : "No",
                };
            })
            .ToList();

        return new ReportData(rows)
        {
            Summary =
            [
                new("deaths", "Deaths", list.Count(r => r.Outcome == DischargeType.Expired), ReportValueType.Integer),
                new("lama", "LAMA", list.Count(r => r.Outcome == DischargeType.Lama), ReportValueType.Integer),
                new("dama", "DAMA", list.Count(r => r.Outcome == DischargeType.Dama), ReportValueType.Integer),
                new("absconded", "Absconded", list.Count(r => r.Outcome == DischargeType.Absconded), ReportValueType.Integer),
            ],
            PatientIds = list.Select(r => r.PatientId).Distinct().ToList(),
        };
    }

    private sealed record RegisterRow(
        Guid PatientId, string AdmissionNumber, string Uhid, string PatientName, Gender Gender, DateOnly? DateOfBirth, Guid? DepartmentId, Guid? ConsultantId,
        DateTimeOffset AdmittedAt, DateTimeOffset DischargedAt, DischargeType? Outcome, bool IsMlc);
}
