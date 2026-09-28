using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>IRMS-01/02: incidents by type and harm, with the rate per 1,000 patient-days.</summary>
internal sealed class IncidentRatesReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "incident-rates",
        "Incident rates",
        ReportCategory.Quality,
        "Incidents by type with harm level, sentinel events, closure and the rate per 1,000 patient-days.",
        "Incidents by the date they occurred. Harm = mild, moderate, severe or death. Rate = incidents ÷ inpatient days (midnight census in the period) × 1,000.",
        Audience.Operations,
        [P.Period(), P.Facility(), P.Department()],
        [
            C.Text("type", "Incident type"),
            C.Int("incidents", "Incidents"),
            C.Int("nearMiss", "Near miss"),
            C.Int("noHarm", "No harm"),
            C.Int("harm", "Harm"),
            C.Int("sentinel", "Sentinel"),
            C.Int("closed", "Closed"),
            C.Pct("closedPercent", "Closed %"),
            C.Dec("rate", "Per 1,000 patient-days"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "type", ["nearMiss", "noHarm", "harm"]),
        DefaultSort: "incidents",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var departmentId = ctx.Args.DepartmentId;
        var groups = await ctx.ListAsync(
            ctx.Db.Set<Incident>().AsNoTracking()
                .Where(i => i.OccurredAt >= start && i.OccurredAt < end && (departmentId == null || i.DepartmentId == departmentId))
                .GroupBy(i => i.Type)
                .Select(g => new IncidentRow(
                    g.Key,
                    g.Count(),
                    g.Count(i => i.Severity == IncidentSeverity.NearMiss),
                    g.Count(i => i.Severity == IncidentSeverity.NoHarm),
                    g.Count(i => i.Severity == IncidentSeverity.Mild || i.Severity == IncidentSeverity.Moderate || i.Severity == IncidentSeverity.Severe
                                 || i.Severity == IncidentSeverity.Death),
                    g.Count(i => i.IsSentinel),
                    g.Count(i => i.Status == IncidentStatus.Closed))),
            ct);
        var stays = await ctx.MeasurementsAsync(
            InpatientData.AdmissionStays(ctx.Db, start, end, departmentId), ct);
        var patientDays = InpatientData.CensusDays(stays, ctx.Args.From, ctx.Args.To);

        var rows = groups
            .Select(g => new object?[]
            {
                Labels.Humanize(g.Type.ToString()), g.Incidents, g.NearMiss, g.NoHarm, g.Harm, g.Sentinel, g.Closed, KpiMath.Percent(g.Closed, g.Incidents),
                KpiMath.PerThousand(g.Incidents, patientDays),
            })
            .ToList();

        var incidents = groups.Sum(g => g.Incidents);
        var closed = groups.Sum(g => g.Closed);
        return new ReportData(rows)
        {
            Totals =
            [
                "All incidents", incidents, groups.Sum(g => g.NearMiss), groups.Sum(g => g.NoHarm), groups.Sum(g => g.Harm), groups.Sum(g => g.Sentinel), closed,
                KpiMath.Percent(closed, incidents), KpiMath.PerThousand(incidents, patientDays),
            ],
            Summary =
            [
                new("incidents", "Incidents", incidents, ReportValueType.Integer),
                new("rate", "Per 1,000 patient-days", KpiMath.PerThousand(incidents, patientDays), ReportValueType.Decimal),
                new("sentinel", "Sentinel events", groups.Sum(g => g.Sentinel), ReportValueType.Integer),
                new("closedPercent", "Closed", KpiMath.Percent(closed, incidents), ReportValueType.Percent),
            ],
            Notes = [string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Inpatient days in the period: {patientDays:N0}.")],
        };
    }

    private sealed record IncidentRow(IncidentType Type, int Incidents, int NearMiss, int NoHarm, int Harm, int Sentinel, int Closed);
}

/// <summary>IRMS-04: CAPA closure and timeliness.</summary>
internal sealed class CapaClosureReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "capa-closure",
        "CAPA closure",
        ReportCategory.Quality,
        "Corrective and preventive actions raised in the period by source: closed, closed on time, overdue and verified effective.",
        "CAPAs raised in the period. Closed = completed or verified. On time = completed on or before the due date (IST). Overdue = still open after the due date. Closure % = closed ÷ (raised − cancelled).",
        Audience.Operations,
        [P.Period(ReportPeriods.ThisQuarter), P.Facility()],
        [
            C.Text("source", "Raised from"),
            C.Int("raised", "Raised"),
            C.Int("closed", "Closed"),
            C.Int("onTime", "Closed on time"),
            C.Int("overdue", "Overdue"),
            C.Int("cancelled", "Cancelled"),
            C.Int("effective", "Verified effective"),
            C.Pct("closure", "Closure %"),
            C.Pct("onTimePercent", "On time %"),
            C.Days("daysToClose", "Average days to close"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "source", ["onTime", "overdue"]),
        DefaultSort: "raised",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var today = ctx.Today;
        var groups = await ctx.ListAsync(
            ctx.Db.Set<Capa>().AsNoTracking()
                .Where(c => c.CreatedAt >= start && c.CreatedAt < end)
                .GroupBy(c => c.Source)
                .Select(g => new CapaRow(
                    g.Key,
                    g.Count(),
                    g.Count(c => c.Status == CapaStatus.Completed || c.Status == CapaStatus.Verified),
                    g.Count(c => (c.Status == CapaStatus.Completed || c.Status == CapaStatus.Verified) && c.CompletedAt != null
                                 && DateOnly.FromDateTime(c.CompletedAt.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime) <= c.DueDate),
                    g.Count(c => (c.Status == CapaStatus.Open || c.Status == CapaStatus.InProgress) && c.DueDate < today),
                    g.Count(c => c.Status == CapaStatus.Cancelled),
                    g.Count(c => c.IsEffective == true),
                    g.Average(c => (c.Status == CapaStatus.Completed || c.Status == CapaStatus.Verified) && c.CompletedAt != null
                        ? (double?)(c.CompletedAt.Value - c.CreatedAt).TotalDays
                        : null))),
            ct);

        var rows = groups.Select(g => Row(Labels.Humanize(g.Source.ToString()), g)).ToList();
        var total = new CapaRow(
            default, groups.Sum(g => g.Raised), groups.Sum(g => g.Closed), groups.Sum(g => g.OnTime), groups.Sum(g => g.Overdue), groups.Sum(g => g.Cancelled),
            groups.Sum(g => g.Effective), null);
        return new ReportData(rows)
        {
            Totals = Row("All sources", total),
            Summary =
            [
                new("raised", "CAPAs raised", total.Raised, ReportValueType.Integer),
                new("closure", "Closed", KpiMath.Percent(total.Closed, total.Raised - total.Cancelled), ReportValueType.Percent),
                new("onTimePercent", "Closed on time", KpiMath.Percent(total.OnTime, total.Closed), ReportValueType.Percent),
                new("overdue", "Overdue now", total.Overdue, ReportValueType.Integer),
            ],
        };
    }

    private static object?[] Row(string label, CapaRow g) =>
    [
        label, g.Raised, g.Closed, g.OnTime, g.Overdue, g.Cancelled, g.Effective, KpiMath.Percent(g.Closed, g.Raised - g.Cancelled), KpiMath.Percent(g.OnTime, g.Closed),
        Figures.Round1(g.AverageDaysToClose),
    ];

    private sealed record CapaRow(CapaSource Source, int Raised, int Closed, int OnTime, int Overdue, int Cancelled, int Effective, double? AverageDaysToClose);
}

/// <summary>IRMS-06: NABH quality indicators recorded for the period, against their targets.</summary>
internal sealed class NabhIndicatorsReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "nabh-indicators",
        "NABH quality indicators",
        ReportCategory.Quality,
        "Every active quality indicator with its latest value in the period, the period average, the target and the trend.",
        "Indicator values recorded for reporting periods that start within the chosen dates. Latest = the most recent period; average = mean of the periods; trend compares the latest value with the average of up to three earlier values in the indicator's direction (better, stable or worse).",
        Audience.Operations,
        [P.Period(ReportPeriods.ThisFinancialYear), P.Facility()],
        [
            C.Text("code", "Code"),
            C.Text("indicator", "Indicator"),
            C.Text("category", "Category"),
            C.Text("nabh", "NABH reference"),
            C.Text("unit", "Unit"),
            C.Dec("target", "Target"),
            C.Dec("latest", "Latest value"),
            C.Text("latestPeriod", "Latest period"),
            C.Dec("average", "Average"),
            C.Int("periods", "Periods reported"),
            C.Text("meetsTarget", "Meets target"),
            C.Text("trend", "Trend"),
        ],
        null);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (periodFrom, periodTo) = (ctx.Args.From, ctx.Args.To);
        var indicators = await ctx.ListAsync(
            ctx.Db.Set<QualityIndicator>().AsNoTracking().Where(i => i.IsActive)
                .Select(i => new IndicatorRow(i.Id, i.Code, i.Name, i.Category, i.NabhReference, i.Unit, i.Target, i.Direction, i.Frequency, i.SortOrder)),
            ct);
        var values = await ctx.MeasurementsAsync(
            ctx.Db.Set<IndicatorValue>().AsNoTracking()
                .Where(v => v.PeriodStart >= periodFrom && v.PeriodStart <= periodTo)
                .Select(v => new ValueRow(v.IndicatorId, v.PeriodStart, v.Value, v.MeetsTarget)),
            ct);

        var byIndicator = values.GroupBy(v => v.IndicatorId).ToDictionary(g => g.Key, g => g.OrderBy(v => v.PeriodStart).ToList());
        var rows = indicators
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
            .Select(i =>
            {
                var series = byIndicator.GetValueOrDefault(i.Id) ?? [];
                var latest = series.LastOrDefault();
                var trend = IndicatorMath.Trend(series.Select(v => v.Value).ToList(), i.Direction);
                return new object?[]
                {
                    i.Code, i.Name, i.Category, i.NabhReference, Labels.Humanize(i.Unit.ToString()), i.Target, latest?.Value,
                    latest is null ? "No data" : IndicatorMath.PeriodLabel(latest.PeriodStart, i.Frequency),
                    series.Count == 0 ? null : Math.Round(series.Average(v => v.Value), 2, MidpointRounding.AwayFromZero), series.Count,
                    latest?.MeetsTarget switch { true => "Yes", false => "No", _ => "—" },
                    trend == TrendDirection.InsufficientData ? "—" : trend switch { TrendDirection.Improving => "Better", TrendDirection.Worsening => "Worse", _ => "Stable" },
                };
            })
            .ToList();

        var latestValues = indicators.Select(i => byIndicator.GetValueOrDefault(i.Id)?.LastOrDefault()).OfType<ValueRow>().ToList();
        return new ReportData(rows)
        {
            Summary =
            [
                new("indicators", "Indicators", indicators.Count, ReportValueType.Integer),
                new("reported", "Reported in the period", byIndicator.Count, ReportValueType.Integer),
                new("meeting", "Meeting target", KpiMath.Percent(latestValues.Count(v => v.MeetsTarget == true), latestValues.Count(v => v.MeetsTarget is not null)), ReportValueType.Percent),
            ],
        };
    }

    private sealed record IndicatorRow(
        Guid Id, string Code, string Name, string Category, string? NabhReference, IndicatorUnit Unit, decimal? Target, IndicatorDirection Direction,
        IndicatorFrequency Frequency, int SortOrder);

    private sealed record ValueRow(Guid IndicatorId, DateOnly PeriodStart, decimal Value, bool? MeetsTarget);
}

/// <summary>MRD-06: disease (morbidity) index — the most frequent coded diagnoses.</summary>
internal sealed class TopDiagnosesReport : ReportDefinition
{
    private const string TopKey = "top";

    public override ReportDescriptor Descriptor { get; } = new(
        "top-diagnoses",
        "Top diagnoses (morbidity index)",
        ReportCategory.Quality,
        "The most frequent ICD-coded diagnoses in the period, with the number of patients and the out-patient / in-patient split.",
        "Provisional and final diagnoses with an ICD code recorded in encounters in the period (differential diagnoses excluded). Patients = distinct patients with the code.",
        Audience.Operations,
        [P.Period(), P.Facility(), P.Department(), P.Number(TopKey, "Number of diagnoses", 50, 10, 500)],
        [
            C.Text("code", "ICD code"),
            C.Text("diagnosis", "Diagnosis"),
            C.Int("count", "Diagnoses"),
            C.Int("patients", "Patients"),
            C.Int("outPatient", "Out-patient"),
            C.Int("inPatient", "In-patient"),
            C.Pct("share", "Share %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "code", ["count"]),
        DefaultSort: "count",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var departmentId = ctx.Args.DepartmentId;
        var top = ctx.Args.NumberOf(TopKey) ?? 50;
        var coded =
            from d in ctx.Db.Set<Diagnosis>().AsNoTracking()
            join e in ctx.Db.Set<Encounter>() on d.EncounterId equals (Guid?)e.Id
            where d.RecordedAt >= start && d.RecordedAt < end && d.Type != DiagnosisType.Differential && d.IcdCode != null
                  && (departmentId == null || e.DepartmentId == departmentId)
            select new { Code = d.IcdCode!, d.Title, d.PatientId, InPatient = e.Type == EncounterType.InPatient };
        var groups = await ctx.ListAsync(
            coded.GroupBy(x => x.Code)
                .OrderByDescending(g => g.Count())
                .Take(top)
                .Select(g => new DiagnosisRow(g.Key, g.Max(x => x.Title)!, g.Count(), g.Select(x => x.PatientId).Distinct().Count(), g.Count(x => !x.InPatient), g.Count(x => x.InPatient))),
            ct);
        var total = await ctx.SingleAsync(coded.GroupBy(x => 1).Select(g => new TotalRow(g.Count(), g.Select(x => x.PatientId).Distinct().Count())), ct);

        var all = total?.Count ?? 0;
        var rows = groups
            .Select(g => new object?[] { g.Code, g.Title, g.Count, g.Patients, g.OutPatient, g.InPatient, KpiMath.Percent(g.Count, all) })
            .ToList();
        return new ReportData(rows)
        {
            Totals = ["All coded diagnoses", null, all, total?.Patients, null, null, all == 0 ? null : 100m],
            Summary =
            [
                new("count", "Coded diagnoses", all, ReportValueType.Integer),
                new("patients", "Patients", total?.Patients ?? 0, ReportValueType.Integer),
                new("top", "Most frequent", groups.FirstOrDefault() is { } first ? $"{first.Code} {first.Title}" : null, ReportValueType.Text),
            ],
        };
    }

    private sealed record DiagnosisRow(string Code, string Title, int Count, int Patients, int OutPatient, int InPatient);

    private sealed record TotalRow(int Count, int Patients);
}
