using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>Shared out-patient filters: visit type (in-patient visits are never OP census), department and doctor.</summary>
internal static class OutPatientData
{
    public const string AllTypes = "all";

    public static readonly IReadOnlyList<ReportChoice> VisitTypes =
    [
        new(AllTypes, "All out-patient types"),
        new(nameof(VisitType.OutPatient), "Out-patient consultation"),
        new(nameof(VisitType.Emergency), "Emergency"),
        new(nameof(VisitType.DayCare), "Day care"),
        new(nameof(VisitType.Telehealth), "Telehealth"),
        new(nameof(VisitType.HealthCheckup), "Health checkup"),
    ];

    public static ReportParameter VisitTypeParameter() => P.Choice(ReportKeys.VisitType, "Visit type", VisitTypes, nameof(VisitType.OutPatient));

    public static IQueryable<Visit> Visits(ReportContext ctx)
    {
        var start = ctx.Args.Start;
        var end = ctx.Args.End;
        var visits = ctx.Db.Set<Visit>().AsNoTracking().Where(v => v.VisitedAt >= start && v.VisitedAt < end && v.Type != VisitType.InPatient);
        if (ctx.Args.EnumOf<VisitType>(ReportKeys.VisitType) is { } type)
        {
            visits = visits.Where(v => v.Type == type);
        }

        if (ctx.Args.DepartmentId is { } departmentId)
        {
            visits = visits.Where(v => v.DepartmentId == departmentId);
        }

        if (ctx.Args.PractitionerId is { } practitionerId)
        {
            visits = visits.Where(v => v.PractitionerId == practitionerId);
        }

        return visits;
    }
}

/// <summary>MIS-01: OP census by department and doctor, new vs follow-up.</summary>
internal sealed class OpCensusReport : ReportDefinition
{
    private const string ByDepartment = "department";
    private const string ByDoctor = "doctor";

    public override ReportDescriptor Descriptor { get; } = new(
        "op-census",
        "OP census by department and doctor",
        ReportCategory.OutPatient,
        "Out-patient visits in the period by department or by doctor, split into new and follow-up visits.",
        "Visits = check-ins in the period, excluding cancelled visits (shown separately). Follow-up = a repeat consultation with the same doctor within the doctor's follow-up validity. Visits per day = visits ÷ days in the period.",
        Audience.Operations,
        [
            P.Period(),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByDepartment, "Department"), new(ByDoctor, "Doctor")], ByDepartment),
            OutPatientData.VisitTypeParameter(),
            P.Department(),
            P.Doctor(),
        ],
        [
            C.Text("department", "Department"),
            C.Text("doctor", "Doctor"),
            C.Int("visits", "Visits"),
            C.Int("new", "New"),
            C.Int("followUp", "Follow-up"),
            C.Pct("followUpPercent", "Follow-up %"),
            C.Dec("perDay", "Visits per day"),
            C.Int("cancelled", "Cancelled"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "department", ["new", "followUp"]),
        DefaultSort: "visits",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var byDoctor = ctx.Args.Text(ReportKeys.GroupBy) == ByDoctor;
        var visits = OutPatientData.Visits(ctx);
        var query = byDoctor
            ? visits.GroupBy(v => new { v.DepartmentId, v.PractitionerId })
                .Select(g => new CensusRow(
                    g.Key.DepartmentId,
                    g.Key.PractitionerId,
                    g.Count(v => v.Status != VisitStatus.Cancelled),
                    g.Count(v => v.Status != VisitStatus.Cancelled && v.IsFollowUp),
                    g.Count(v => v.Status == VisitStatus.Cancelled)))
            : visits.GroupBy(v => v.DepartmentId)
                .Select(g => new CensusRow(
                    g.Key,
                    null,
                    g.Count(v => v.Status != VisitStatus.Cancelled),
                    g.Count(v => v.Status != VisitStatus.Cancelled && v.IsFollowUp),
                    g.Count(v => v.Status == VisitStatus.Cancelled)));

        var groups = await ctx.ListAsync(query, ct);
        var departments = await Names.DepartmentsAsync(ctx, groups.Select(g => g.DepartmentId), ct);
        var doctors = await Names.PractitionersAsync(ctx, groups.Select(g => g.PractitionerId), ct);
        var days = ctx.Args.DayCount;

        var rows = groups
            .Select(g => new object?[]
            {
                Labels.Name(departments, g.DepartmentId),
                byDoctor ? Labels.Name(doctors, g.PractitionerId, "No doctor assigned") : "All doctors",
                g.Visits,
                g.Visits - g.FollowUps,
                g.FollowUps,
                KpiMath.Percent(g.FollowUps, g.Visits),
                Figures.PerDay(g.Visits, days),
                g.Cancelled,
            })
            .ToList();

        var visitsTotal = groups.Sum(g => g.Visits);
        var followUps = groups.Sum(g => g.FollowUps);
        return new ReportData(rows)
        {
            Totals = ["Total", null, visitsTotal, visitsTotal - followUps, followUps, KpiMath.Percent(followUps, visitsTotal), Figures.PerDay(visitsTotal, days), groups.Sum(g => g.Cancelled)],
            Summary =
            [
                new("visits", "OP visits", visitsTotal, ReportValueType.Integer),
                new("new", "New visits", visitsTotal - followUps, ReportValueType.Integer),
                new("followUp", "Follow-up visits", followUps, ReportValueType.Integer),
                new("perDay", "Visits per day", Figures.PerDay(visitsTotal, days), ReportValueType.Decimal),
            ],
            Chart = byDoctor ? new ReportChartHint(ReportChartType.StackedBar, "doctor", ["new", "followUp"]) : null,
            RowKeys = byDoctor ? null : groups.Select(g => g.DepartmentId?.ToString()).ToList(),
            DrillDown = byDoctor ? null : DoctorDrill,
        };
    }

    private static readonly ReportDrillDown DoctorDrill =
        new("op-census", ReportKeys.Department, "See doctors", new Dictionary<string, string> { [ReportKeys.GroupBy] = ByDoctor });

    private sealed record CensusRow(Guid? DepartmentId, Guid? PractitionerId, int Visits, int FollowUps, int Cancelled);
}

/// <summary>MIS-01: new vs follow-up visits over time (day, week or month).</summary>
internal sealed class OpVisitTrendReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "op-visit-trend",
        "New vs follow-up visits (trend)",
        ReportCategory.OutPatient,
        "Daily, weekly or monthly out-patient visits, split into new and follow-up visits.",
        "Visits = check-ins on each IST day, excluding cancelled visits. Follow-up = a repeat consultation with the same doctor within the follow-up validity; all other visits are new.",
        Audience.Operations,
        [P.Period(ReportPeriods.Last30Days), P.Facility(), P.Interval(), OutPatientData.VisitTypeParameter(), P.Department(), P.Doctor()],
        [C.Date("period", "Date"), C.Int("new", "New"), C.Int("followUp", "Follow-up"), C.Int("visits", "Total visits"), C.Pct("followUpPercent", "Follow-up %")],
        new ReportChartHint(ReportChartType.StackedBar, "period", ["new", "followUp"], TimeSeries: true));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var daily = await ctx.ListAsync(
            OutPatientData.Visits(ctx)
                .Where(v => v.Status != VisitStatus.Cancelled)
                .GroupBy(v => DateOnly.FromDateTime(v.VisitedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                .Select(g => new DayCount(g.Key, g.Count(v => !v.IsFollowUp), g.Count(v => v.IsFollowUp))),
            ct);

        var interval = Trend.Of(ctx.Args);
        var byBucket = daily.GroupBy(d => Trend.BucketOf(d.Day, interval))
            .ToDictionary(g => g.Key, g => (New: g.Sum(d => d.New), FollowUp: g.Sum(d => d.FollowUp)));

        var rows = Trend.Buckets(ctx.Args.From, ctx.Args.To, interval)
            .Select(bucket =>
            {
                var (newVisits, followUps) = byBucket.GetValueOrDefault(bucket);
                return new object?[] { bucket, newVisits, followUps, newVisits + followUps, KpiMath.Percent(followUps, newVisits + followUps) };
            })
            .ToList();

        var totalNew = daily.Sum(d => d.New);
        var totalFollowUp = daily.Sum(d => d.FollowUp);
        return new ReportData(rows)
        {
            Columns = TrendColumns(interval),
            Totals = ["Total", totalNew, totalFollowUp, totalNew + totalFollowUp, KpiMath.Percent(totalFollowUp, totalNew + totalFollowUp)],
            Summary =
            [
                new("visits", "OP visits", totalNew + totalFollowUp, ReportValueType.Integer),
                new("perDay", "Visits per day", Figures.PerDay(totalNew + totalFollowUp, ctx.Args.DayCount), ReportValueType.Decimal),
                new("followUpPercent", "Follow-up %", KpiMath.Percent(totalFollowUp, totalNew + totalFollowUp), ReportValueType.Percent),
            ],
        };
    }

    private IReadOnlyList<ReportColumn> TrendColumns(string interval) =>
    [
        new ReportColumn("period", Trend.ColumnLabel(interval), interval == Trend.Month ? ReportValueType.Month : ReportValueType.Date),
        .. Descriptor.Columns.Skip(1),
    ];

    private sealed record DayCount(DateOnly Day, int New, int FollowUp);
}

/// <summary>Hourly footfall: when patients arrive, by IST hour and weekday (staffing of counters and OPD).</summary>
internal sealed class OpHourlyFootfallReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "op-hourly-footfall",
        "Hourly footfall",
        ReportCategory.OutPatient,
        "Patient check-ins by hour of the day (IST) and weekday, to plan counter and OPD staffing.",
        "Check-ins = visits created in each IST hour, excluding cancelled visits. Average per day = check-ins in the hour ÷ days in the period. Share = the hour's check-ins ÷ all check-ins.",
        Audience.Operations,
        [P.Period(ReportPeriods.Last30Days), P.Facility(), OutPatientData.VisitTypeParameter(), P.Department()],
        [
            C.Text("hour", "Hour"),
            C.Int("visits", "Check-ins"),
            C.Dec("perDay", "Average per day"),
            C.Pct("share", "Share %"),
            C.Int("mon", "Mon"),
            C.Int("tue", "Tue"),
            C.Int("wed", "Wed"),
            C.Int("thu", "Thu"),
            C.Int("fri", "Fri"),
            C.Int("sat", "Sat"),
            C.Int("sun", "Sun"),
        ],
        new ReportChartHint(ReportChartType.Bar, "hour", ["perDay"], TimeSeries: true));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var cells = await ctx.ListAsync(
            OutPatientData.Visits(ctx)
                .Where(v => v.Status != VisitStatus.Cancelled)
                .GroupBy(v => new
                {
                    v.VisitedAt.AddMinutes(AnalyticsClock.OffsetMinutes).Hour,
                    v.VisitedAt.AddMinutes(AnalyticsClock.OffsetMinutes).DayOfWeek,
                })
                .Select(g => new HourCell(g.Key.Hour, g.Key.DayOfWeek, g.Count())),
            ct);

        var total = cells.Sum(c => c.Count);
        var days = ctx.Args.DayCount;
        var rows = new List<object?[]>();
        if (cells.Count > 0)
        {
            var first = cells.Min(c => c.Hour);
            var last = cells.Max(c => c.Hour);
            for (var hour = first; hour <= last; hour++)
            {
                var inHour = cells.Where(c => c.Hour == hour).ToList();
                var count = inHour.Sum(c => c.Count);
                int Day(DayOfWeek day) => inHour.Where(c => c.Day == day).Sum(c => c.Count);
                rows.Add(
                [
                    $"{hour:00}:00–{(hour + 1) % 24:00}:00",
                    count,
                    Figures.PerDay(count, days),
                    KpiMath.Percent(count, total),
                    Day(DayOfWeek.Monday),
                    Day(DayOfWeek.Tuesday),
                    Day(DayOfWeek.Wednesday),
                    Day(DayOfWeek.Thursday),
                    Day(DayOfWeek.Friday),
                    Day(DayOfWeek.Saturday),
                    Day(DayOfWeek.Sunday),
                ]);
            }
        }

        var peak = cells.GroupBy(c => c.Hour).OrderByDescending(g => g.Sum(c => c.Count)).FirstOrDefault();
        int Weekday(DayOfWeek day) => cells.Where(c => c.Day == day).Sum(c => c.Count);
        return new ReportData(rows)
        {
            Totals =
            [
                "Total", total, Figures.PerDay(total, days), total > 0 ? 100m : null, Weekday(DayOfWeek.Monday), Weekday(DayOfWeek.Tuesday),
                Weekday(DayOfWeek.Wednesday), Weekday(DayOfWeek.Thursday), Weekday(DayOfWeek.Friday), Weekday(DayOfWeek.Saturday), Weekday(DayOfWeek.Sunday),
            ],
            Summary =
            [
                new("visits", "Check-ins", total, ReportValueType.Integer),
                new("peakHour", "Busiest hour", peak is null ? null : $"{peak.Key:00}:00–{(peak.Key + 1) % 24:00}:00", ReportValueType.Text),
                new("perDay", "Check-ins per day", Figures.PerDay(total, days), ReportValueType.Decimal),
            ],
            Notes = ["Weekday columns count all check-ins on that weekday in the period (IST)."],
        };
    }

    private sealed record HourCell(int Hour, DayOfWeek Day, int Count);
}

/// <summary>Appointment no-show rate by doctor (OA-*, PMS-02).</summary>
internal sealed class AppointmentNoShowReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "appointment-no-show",
        "Appointment no-show rate",
        ReportCategory.OutPatient,
        "Booked appointments per doctor and how many patients came, did not come or cancelled.",
        "Appointments due in the period (by slot time), excluding rescheduled bookings (their replacement counts). Attended = checked in, in consultation or completed. No-show rate = no-shows ÷ (attended + no-shows). Not marked = still booked after the slot.",
        Audience.Operations,
        [P.Period(), P.Facility(), P.Department(), P.Doctor()],
        [
            C.Text("doctor", "Doctor"),
            C.Text("department", "Department"),
            C.Int("booked", "Booked"),
            C.Int("attended", "Attended"),
            C.Int("noShow", "No-show"),
            C.Int("cancelled", "Cancelled"),
            C.Int("notMarked", "Not marked"),
            C.Pct("noShowPercent", "No-show %"),
            C.Pct("cancelledPercent", "Cancelled %"),
            C.Int("online", "Booked online"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "doctor", ["attended", "noShow", "cancelled"]),
        DefaultSort: "booked",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var start = ctx.Args.Start;
        var end = ctx.Args.End;
        var appointments = ctx.Db.Set<Appointment>().AsNoTracking()
            .Where(a => a.StartsAt >= start && a.StartsAt < end && a.Status != AppointmentStatus.Rescheduled);
        if (ctx.Args.DepartmentId is { } departmentId)
        {
            appointments = appointments.Where(a => a.DepartmentId == departmentId);
        }

        if (ctx.Args.PractitionerId is { } practitionerId)
        {
            appointments = appointments.Where(a => a.PractitionerId == practitionerId);
        }

        var groups = await ctx.ListAsync(
            appointments.GroupBy(a => new { a.PractitionerId, a.DepartmentId })
                .Select(g => new NoShowRow(
                    g.Key.PractitionerId,
                    g.Key.DepartmentId,
                    g.Count(),
                    g.Count(a => a.Status == AppointmentStatus.CheckedIn || a.Status == AppointmentStatus.InConsultation || a.Status == AppointmentStatus.Completed),
                    g.Count(a => a.Status == AppointmentStatus.NoShow),
                    g.Count(a => a.Status == AppointmentStatus.Cancelled),
                    g.Count(a => a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed),
                    g.Count(a => a.Channel == AppointmentChannel.Online || a.Channel == AppointmentChannel.Portal || a.Channel == AppointmentChannel.WhatsApp))),
            ct);

        var doctors = await Names.PractitionersAsync(ctx, groups.Select(g => (Guid?)g.PractitionerId), ct);
        var departments = await Names.DepartmentsAsync(ctx, groups.Select(g => (Guid?)g.DepartmentId), ct);
        var rows = groups
            .Select(g => new object?[]
            {
                Labels.Name(doctors, g.PractitionerId), Labels.Name(departments, g.DepartmentId), g.Booked, g.Attended, g.NoShow, g.Cancelled, g.NotMarked,
                KpiMath.NoShowRate(g.NoShow, g.Attended), KpiMath.Percent(g.Cancelled, g.Booked), g.Online,
            })
            .ToList();

        var booked = groups.Sum(g => g.Booked);
        var attended = groups.Sum(g => g.Attended);
        var noShow = groups.Sum(g => g.NoShow);
        var cancelled = groups.Sum(g => g.Cancelled);
        return new ReportData(rows)
        {
            Totals = ["Total", null, booked, attended, noShow, cancelled, groups.Sum(g => g.NotMarked), KpiMath.NoShowRate(noShow, attended), KpiMath.Percent(cancelled, booked), groups.Sum(g => g.Online)],
            Summary =
            [
                new("booked", "Appointments", booked, ReportValueType.Integer),
                new("noShowPercent", "No-show rate", KpiMath.NoShowRate(noShow, attended), ReportValueType.Percent),
                new("cancelledPercent", "Cancellation rate", KpiMath.Percent(cancelled, booked), ReportValueType.Percent),
                new("onlinePercent", "Booked online", KpiMath.Percent(groups.Sum(g => g.Online), booked), ReportValueType.Percent),
            ],
        };
    }

    private sealed record NoShowRow(Guid PractitionerId, Guid DepartmentId, int Booked, int Attended, int NoShow, int Cancelled, int NotMarked, int Online);
}
