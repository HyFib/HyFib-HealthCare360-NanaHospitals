using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using Theatre = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>OT-07: theatre utilisation, first-case on-time starts and turnover (the OT committee's monthly numbers).</summary>
internal sealed class OtUtilisationReport : ReportDefinition
{
    private const string MinutesKey = "minutesPerDay";

    public override ReportDescriptor Descriptor { get; } = new(
        "ot-utilisation",
        "OT utilisation",
        ReportCategory.SpecialtyCare,
        "Cases, booked and actual theatre time, utilisation, first-case on-time starts and turnover time for each operation theatre.",
        "Cases scheduled in the period. Actual minutes = patient in room to out of room for completed cases. Utilisation % = actual minutes ÷ (days × planned OT minutes per day). A first case is on time when the patient is in the room within 15 minutes of the scheduled start. Turnover = gap between consecutive cases (gaps over 2 hours are idle time, not turnover).",
        Audience.Operations,
        [P.Period(), P.Facility(), P.Theatre(), P.Number(MinutesKey, "Planned OT minutes per day", 480, 60, 1440)],
        [
            C.Text("theatre", "Theatre"),
            C.Int("completed", "Cases done"),
            C.Int("cancelled", "Cancelled"),
            C.Int("postponed", "Postponed"),
            C.Int("bookedMinutes", "Booked minutes"),
            C.Int("actualMinutes", "Actual minutes"),
            C.Int("availableMinutes", "Available minutes"),
            C.Pct("utilisation", "Utilisation %"),
            C.Pct("onTime", "First case on time %"),
            C.Minutes("turnover", "Average turnover"),
        ],
        new ReportChartHint(ReportChartType.Bar, "theatre", ["utilisation"]),
        DefaultSort: "utilisation",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var theatreId = ctx.Args.TheatreId;
        var cases = await ctx.MeasurementsAsync(
            ctx.Db.Set<SurgeryBooking>().AsNoTracking()
                .Where(b => b.TheatreId != null && b.ScheduledStart >= start && b.ScheduledStart < end && b.ScheduledEnd != null
                            && (theatreId == null || b.TheatreId == theatreId))
                .Select(b => new CaseRow(b.TheatreId!.Value, b.Status, b.ScheduledStart!.Value, b.ScheduledEnd!.Value, b.InRoomAt ?? b.StartedAt, b.CompletedAt)),
            ct);
        var theatres = await ctx.ListAsync(
            ctx.Db.Set<Theatre>().AsNoTracking().Where(t => theatreId == null || t.Id == theatreId)
                .Select(t => new TheatreRow(t.Id, t.Code, t.Name, t.DisplayOrder, t.IsActive)),
            ct);

        var timings = cases
            .Select(c => new OtCaseTiming(c.TheatreId, AnalyticsClock.LocalDate(c.Start), c.Status, c.Start, c.End, c.InRoom, c.OutOfRoom))
            .ToList();
        var available = ctx.Args.DayCount * (ctx.Args.NumberOf(MinutesKey) ?? 480);
        var used = timings.Select(t => t.TheatreId).ToHashSet();
        var results = theatres
            .Where(t => t.IsActive || used.Contains(t.Id))
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Code, StringComparer.OrdinalIgnoreCase)
            .Select(t => (Theatre: t, Result: OtUtilisation.Calculate(t.Id, timings, available)))
            .ToList();

        var rows = results
            .Select(r => new object?[]
            {
                $"{r.Theatre.Code} · {r.Theatre.Name}", r.Result.CasesCompleted, r.Result.CasesCancelled, r.Result.CasesPostponed, r.Result.BookedMinutes,
                r.Result.ActualMinutes, r.Result.AvailableMinutes, Figures.Round1(r.Result.UtilisationPercent), Figures.Round1(r.Result.FirstCaseOnTimePercent),
                Figures.Round1(r.Result.AverageTurnoverMinutes),
            })
            .ToList();

        var actual = results.Sum(r => r.Result.ActualMinutes);
        var totalAvailable = results.Sum(r => r.Result.AvailableMinutes);
        var firstCases = results.Sum(r => r.Result.FirstCases);
        var onTime = results.Sum(r => r.Result.FirstCasesOnTime);
        return new ReportData(rows)
        {
            Totals =
            [
                "All theatres", results.Sum(r => r.Result.CasesCompleted), results.Sum(r => r.Result.CasesCancelled), results.Sum(r => r.Result.CasesPostponed),
                results.Sum(r => r.Result.BookedMinutes), actual, totalAvailable, KpiMath.Percent(actual, totalAvailable), KpiMath.Percent(onTime, firstCases), null,
            ],
            Summary =
            [
                new("completed", "Surgeries done", results.Sum(r => r.Result.CasesCompleted), ReportValueType.Integer),
                new("utilisation", "Utilisation", KpiMath.Percent(actual, totalAvailable), ReportValueType.Percent),
                new("onTime", "First case on time", KpiMath.Percent(onTime, firstCases), ReportValueType.Percent),
                new("cancelled", "Cancelled", results.Sum(r => r.Result.CasesCancelled), ReportValueType.Integer),
            ],
        };
    }

    private sealed record CaseRow(Guid TheatreId, SurgeryStatus Status, DateTimeOffset Start, DateTimeOffset End, DateTimeOffset? InRoom, DateTimeOffset? OutOfRoom);

    private sealed record TheatreRow(Guid Id, string Code, string Name, int DisplayOrder, bool IsActive);
}

/// <summary>DIA-03: dialysis sessions done, missed and cancelled.</summary>
internal sealed class DialysisSessionsReport : ReportDefinition
{
    private const string ByDate = "date";
    private const string ByShift = "shift";
    private const string ByMachine = "machine";

    public override ReportDescriptor Descriptor { get; } = new(
        "dialysis-sessions",
        "Dialysis sessions",
        ReportCategory.SpecialtyCare,
        "Scheduled, completed, missed and cancelled dialysis sessions by date, shift or machine, with session length and ultrafiltration.",
        "Sessions scheduled in the period. Completion % = completed ÷ (sessions − cancelled). Average duration and ultrafiltration are over completed sessions that recorded them.",
        Audience.Operations,
        [
            P.Period(),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByDate, "Date"), new(ByShift, "Shift"), new(ByMachine, "Machine")], ByDate),
        ],
        [
            C.Date("key", "Date"),
            C.Int("sessions", "Sessions"),
            C.Int("completed", "Completed"),
            C.Int("missed", "Missed"),
            C.Int("cancelled", "Cancelled"),
            C.Pct("completion", "Completion %"),
            C.Minutes("duration", "Average duration"),
            C.Dec("uf", "Average UF (ml)"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "key", ["completed", "missed", "cancelled"], TimeSeries: true));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (periodFrom, periodTo) = (ctx.Args.From, ctx.Args.To);
        var groupBy = ctx.Args.Text(ReportKeys.GroupBy) ?? ByDate;
        var sessions = ctx.Db.Set<DialysisSession>().AsNoTracking().Where(s => s.ScheduledDate >= periodFrom && s.ScheduledDate <= periodTo);
        var projected = sessions.Select(s => new
        {
            s.ScheduledDate,
            s.Shift,
            s.MachineId,
            s.Status,
            Minutes = s.Status == DialysisSessionStatus.Completed && s.StartedAt != null && s.EndedAt != null ? (double?)(s.EndedAt.Value - s.StartedAt.Value).TotalMinutes : null,
            Uf = s.Status == DialysisSessionStatus.Completed ? s.UfAchievedMl : null,
        });

        List<SessionRow> groups = groupBy switch
        {
            ByShift => await ctx.ListAsync(
                projected.GroupBy(s => s.Shift).Select(g => new SessionRow(
                    null, g.Key, null, g.Count(), g.Count(s => s.Status == DialysisSessionStatus.Completed), g.Count(s => s.Status == DialysisSessionStatus.Missed),
                    g.Count(s => s.Status == DialysisSessionStatus.Cancelled), g.Average(s => s.Minutes), g.Average(s => (double?)s.Uf))),
                ct),
            ByMachine => await ctx.ListAsync(
                projected.GroupBy(s => s.MachineId).Select(g => new SessionRow(
                    null, null, g.Key, g.Count(), g.Count(s => s.Status == DialysisSessionStatus.Completed), g.Count(s => s.Status == DialysisSessionStatus.Missed),
                    g.Count(s => s.Status == DialysisSessionStatus.Cancelled), g.Average(s => s.Minutes), g.Average(s => (double?)s.Uf))),
                ct),
            _ => await ctx.ListAsync(
                projected.GroupBy(s => s.ScheduledDate).Select(g => new SessionRow(
                    g.Key, null, null, g.Count(), g.Count(s => s.Status == DialysisSessionStatus.Completed), g.Count(s => s.Status == DialysisSessionStatus.Missed),
                    g.Count(s => s.Status == DialysisSessionStatus.Cancelled), g.Average(s => s.Minutes), g.Average(s => (double?)s.Uf))),
                ct),
        };

        IReadOnlyList<ReportColumn>? columns = null;
        ReportChartHint? chart = null;
        List<object?[]> rows;
        if (groupBy == ByDate)
        {
            var byDate = groups.Where(g => g.Date is not null).ToDictionary(g => g.Date!.Value);
            rows = Trend.Buckets(periodFrom, periodTo, Trend.Day)
                .Select(day => byDate.TryGetValue(day, out var g) ? Row(day, g) : [day, 0, 0, 0, 0, null, null, null])
                .ToList();
        }
        else if (groupBy == ByShift)
        {
            rows = groups.OrderBy(g => (int)(g.Shift ?? default)).Select(g => Row(Labels.Humanize((g.Shift ?? default).ToString()), g)).ToList();
            columns = [C.Text("key", "Shift"), .. Descriptor.Columns.Skip(1)];
            chart = new ReportChartHint(ReportChartType.StackedBar, "key", ["completed", "missed", "cancelled"], TimeSeries: true);
        }
        else
        {
            var ids = groups.Select(g => g.MachineId).OfType<Guid>().ToList();
            var machines = (await ctx.LookupAsync(
                    ctx.Db.Set<DialysisMachine>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).Where(m => ids.Contains(m.Id))
                        .Select(m => new IdName(m.Id, m.Code + (m.Station == null ? string.Empty : " · " + m.Station))),
                    ct))
                .ToDictionary(m => m.Id, m => m.Name);
            rows = groups.Select(g => Row(Labels.Name(machines, g.MachineId, "No machine assigned"), g)).ToList();
            columns = [C.Text("key", "Machine"), .. Descriptor.Columns.Skip(1)];
            chart = new ReportChartHint(ReportChartType.StackedBar, "key", ["completed", "missed", "cancelled"]);
        }

        var sessionsTotal = groups.Sum(g => g.Sessions);
        var completed = groups.Sum(g => g.Completed);
        var cancelled = groups.Sum(g => g.Cancelled);
        return new ReportData(rows)
        {
            Columns = columns,
            Chart = chart,
            Totals = ["Total", sessionsTotal, completed, groups.Sum(g => g.Missed), cancelled, KpiMath.Percent(completed, sessionsTotal - cancelled), null, null],
            Summary =
            [
                new("completed", "Sessions completed", completed, ReportValueType.Integer),
                new("missed", "Missed sessions", groups.Sum(g => g.Missed), ReportValueType.Integer),
                new("completion", "Completion", KpiMath.Percent(completed, sessionsTotal - cancelled), ReportValueType.Percent),
            ],
        };
    }

    private static object?[] Row(object key, SessionRow g) =>
    [
        key, g.Sessions, g.Completed, g.Missed, g.Cancelled, KpiMath.Percent(g.Completed, g.Sessions - g.Cancelled), Figures.Round1(g.AverageMinutes), Figures.Round1(g.AverageUf),
    ];

    private sealed record SessionRow(
        DateOnly? Date, DialysisShift? Shift, Guid? MachineId, int Sessions, int Completed, int Missed, int Cancelled, double? AverageMinutes, double? AverageUf);
}

/// <summary>Blood group labels as printed on blood bags.</summary>
internal static class BloodLabels
{
    public static string Group(BloodGroup group) => group switch
    {
        BloodGroup.APositive => "A+",
        BloodGroup.ANegative => "A−",
        BloodGroup.BPositive => "B+",
        BloodGroup.BNegative => "B−",
        BloodGroup.ABPositive => "AB+",
        BloodGroup.ABNegative => "AB−",
        BloodGroup.OPositive => "O+",
        BloodGroup.ONegative => "O−",
        BloodGroup.BombayPositive => "Bombay (hh)+",
        BloodGroup.BombayNegative => "Bombay (hh)−",
        _ => "Not grouped",
    };

    public static string Component(BloodComponentType type) => type switch
    {
        BloodComponentType.WholeBlood => "Whole blood",
        BloodComponentType.PRBC => "Packed red cells (PRBC)",
        BloodComponentType.FFP => "Fresh frozen plasma (FFP)",
        BloodComponentType.PlateletConcentrate => "Platelet concentrate",
        BloodComponentType.Cryoprecipitate => "Cryoprecipitate",
        _ => Labels.Humanize(type.ToString()),
    };
}

/// <summary>BB-04: blood bank inventory by group and component right now.</summary>
internal sealed class BloodBankStockReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "blood-bank-stock",
        "Blood bank stock (now)",
        ReportCategory.SpecialtyCare,
        "Units available by blood group and component, with reserved units, units still in quarantine and units expiring within 72 hours.",
        "Available = screened units ready to issue. Reserved = cross-matched and held for a request. Quarantine = grouping or infection screening not complete. Expiring = available units that expire within 72 hours.",
        Audience.Operations,
        [P.Facility()],
        [
            C.Text("group", "Blood group"),
            C.Int("wholeBlood", "Whole blood"),
            C.Int("prbc", "PRBC"),
            C.Int("ffp", "FFP"),
            C.Int("platelets", "Platelets"),
            C.Int("cryo", "Cryo"),
            C.Int("available", "Total available"),
            C.Int("reserved", "Reserved"),
            C.Int("quarantine", "Quarantine"),
            C.Int("expiring", "Expiring in 72 h"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "group", ["wholeBlood", "prbc", "ffp", "platelets", "cryo"], TimeSeries: true));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var soon = ctx.Now.AddHours(72);
        var cells = await ctx.ListAsync(
            ctx.Db.Set<BloodComponent>().AsNoTracking()
                .Where(c => c.Status == BloodUnitStatus.Available || c.Status == BloodUnitStatus.Reserved || c.Status == BloodUnitStatus.Quarantine)
                .GroupBy(c => new { c.BloodGroup, c.ComponentType })
                .Select(g => new StockCell(
                    g.Key.BloodGroup,
                    g.Key.ComponentType,
                    g.Count(c => c.Status == BloodUnitStatus.Available),
                    g.Count(c => c.Status == BloodUnitStatus.Reserved),
                    g.Count(c => c.Status == BloodUnitStatus.Quarantine),
                    g.Count(c => c.Status == BloodUnitStatus.Available && c.ExpiresAt <= soon))),
            ct);

        int Available(IEnumerable<StockCell> list, BloodComponentType type) => list.Where(c => c.Type == type).Sum(c => c.Available);
        object?[] Row(string label, IReadOnlyCollection<StockCell> list) =>
        [
            label, Available(list, BloodComponentType.WholeBlood), Available(list, BloodComponentType.PRBC), Available(list, BloodComponentType.FFP),
            Available(list, BloodComponentType.PlateletConcentrate), Available(list, BloodComponentType.Cryoprecipitate), list.Sum(c => c.Available),
            list.Sum(c => c.Reserved), list.Sum(c => c.Quarantine), list.Sum(c => c.Expiring),
        ];

        var rows = Enum.GetValues<BloodGroup>()
            .Select(group => (Group: group, Cells: cells.Where(c => c.Group == group).ToList()))
            .Where(x => x.Group != BloodGroup.Unknown || x.Cells.Count > 0)
            .Select(x => Row(BloodLabels.Group(x.Group), x.Cells))
            .ToList();
        var lowGroups = Enum.GetValues<BloodGroup>()
            .Where(g => g is BloodGroup.APositive or BloodGroup.BPositive or BloodGroup.OPositive or BloodGroup.ABPositive or BloodGroup.ONegative)
            .Where(g => cells.Where(c => c.Group == g && c.Type == BloodComponentType.PRBC).Sum(c => c.Available) < 2)
            .Select(BloodLabels.Group)
            .ToList();
        return new ReportData(rows)
        {
            Totals = Row("Total", cells),
            Summary =
            [
                new("available", "Units available", cells.Sum(c => c.Available), ReportValueType.Integer),
                new("reserved", "Reserved", cells.Sum(c => c.Reserved), ReportValueType.Integer),
                new("expiring", "Expiring in 72 h", cells.Sum(c => c.Expiring), ReportValueType.Integer),
            ],
            Notes = lowGroups.Count == 0 ? [] : [$"Fewer than 2 packed red cell units available for: {string.Join(", ", lowGroups)}."],
        };
    }

    private sealed record StockCell(BloodGroup Group, BloodComponentType Type, int Available, int Reserved, int Quarantine, int Expiring);
}

/// <summary>BB-05/06: blood components prepared, cross-matched, issued and wasted.</summary>
internal sealed class BloodUsageReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "blood-usage",
        "Blood usage and wastage",
        ReportCategory.SpecialtyCare,
        "Components prepared, cross-matched, issued, returned, discarded and expired in the period, with the cross-match to transfusion (C/T) ratio.",
        "Counts by component and the date of each event. C/T ratio = units cross-matched ÷ units issued (a ratio above 2.0 suggests over-ordering). Wastage % = (discarded + expired) ÷ prepared.",
        Audience.Operations,
        [P.Period(), P.Facility()],
        [
            C.Text("component", "Component"),
            C.Int("prepared", "Prepared"),
            C.Int("crossMatched", "Cross-matched"),
            C.Int("issued", "Issued"),
            C.Int("emergency", "Emergency release"),
            C.Int("returned", "Returned"),
            C.Int("discarded", "Discarded"),
            C.Int("expired", "Expired"),
            C.Dec("ctRatio", "C/T ratio"),
            C.Pct("wastage", "Wastage %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "component", ["prepared", "issued", "discarded", "expired"]));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var components = ctx.Db.Set<BloodComponent>().AsNoTracking();
        var prepared = await ctx.ListAsync(
            components.Where(c => c.PreparedAt >= start && c.PreparedAt < end).GroupBy(c => c.ComponentType).Select(g => new TypeCount(g.Key, g.Count(), 0)), ct);
        var crossMatched = await ctx.ListAsync(
            (from x in ctx.Db.Set<CrossMatch>().AsNoTracking()
             join c in components on x.ComponentId equals c.Id
             where x.PerformedAt >= start && x.PerformedAt < end
             select c.ComponentType)
            .GroupBy(t => t)
            .Select(g => new TypeCount(g.Key, g.Count(), 0)),
            ct);
        var issued = await ctx.ListAsync(
            ctx.Db.Set<BloodIssue>().AsNoTracking().Where(i => i.IssuedAt >= start && i.IssuedAt < end)
                .GroupBy(i => i.ComponentType)
                .Select(g => new TypeCount(g.Key, g.Count(), g.Count(i => i.IsEmergencyRelease))),
            ct);
        var returned = await ctx.ListAsync(
            ctx.Db.Set<BloodIssue>().AsNoTracking().Where(i => i.ReturnedAt >= start && i.ReturnedAt < end)
                .GroupBy(i => i.ComponentType)
                .Select(g => new TypeCount(g.Key, g.Count(), 0)),
            ct);
        var wasted = await ctx.ListAsync(
            components.Where(c => (c.Status == BloodUnitStatus.Discarded && c.DiscardedAt >= start && c.DiscardedAt < end)
                                  || (c.Status == BloodUnitStatus.Expired && c.ExpiresAt >= start && c.ExpiresAt < end))
                .GroupBy(c => c.ComponentType)
                .Select(g => new TypeCount(g.Key, g.Count(c => c.Status == BloodUnitStatus.Discarded), g.Count(c => c.Status == BloodUnitStatus.Expired))),
            ct);

        static int Of(IEnumerable<TypeCount> list, BloodComponentType type) => list.Where(c => c.Type == type).Sum(c => c.Count);
        static int ExtraOf(IEnumerable<TypeCount> list, BloodComponentType type) => list.Where(c => c.Type == type).Sum(c => c.Extra);

        var rows = new List<object?[]>();
        foreach (var type in Enum.GetValues<BloodComponentType>())
        {
            var p = Of(prepared, type);
            var x = Of(crossMatched, type);
            var i = Of(issued, type);
            var discarded = Of(wasted, type);
            var expired = ExtraOf(wasted, type);
            if (p + x + i + discarded + expired + Of(returned, type) == 0)
            {
                continue;
            }

            rows.Add([BloodLabels.Component(type), p, x, i, ExtraOf(issued, type), Of(returned, type), discarded, expired, KpiMath.Ratio(x, i, 2), KpiMath.Percent(discarded + expired, p)]);
        }

        var totalPrepared = prepared.Sum(c => c.Count);
        var totalCross = crossMatched.Sum(c => c.Count);
        var totalIssued = issued.Sum(c => c.Count);
        var totalWasted = wasted.Sum(c => c.Count + c.Extra);
        return new ReportData(rows)
        {
            Totals =
            [
                "Total", totalPrepared, totalCross, totalIssued, issued.Sum(c => c.Extra), returned.Sum(c => c.Count), wasted.Sum(c => c.Count), wasted.Sum(c => c.Extra),
                KpiMath.Ratio(totalCross, totalIssued, 2), KpiMath.Percent(totalWasted, totalPrepared),
            ],
            Summary =
            [
                new("issued", "Units issued", totalIssued, ReportValueType.Integer),
                new("ctRatio", "C/T ratio", KpiMath.Ratio(totalCross, totalIssued, 2), ReportValueType.Decimal),
                new("wastage", "Wastage", KpiMath.Percent(totalWasted, totalPrepared), ReportValueType.Percent),
            ],
        };
    }

    private sealed record TypeCount(BloodComponentType Type, int Count, int Extra);
}
