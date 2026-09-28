using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

internal sealed record KeyMinutes(string Key, double Minutes);

/// <summary>Turnaround helpers shared by the lab and radiology reports.</summary>
internal static class Turnaround
{
    public const string TargetKey = "tatTarget";

    public static ReportParameter TargetParameter(int defaultMinutes) =>
        P.Number(TargetKey, "Turnaround target (minutes)", defaultMinutes, 10, 10_080, "Used for the within-target percentage.");

    public static Dictionary<string, TurnaroundSummary> ByKey(IEnumerable<KeyMinutes> points, double? target) =>
        points.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Percentiles.Summarize(g.Select(p => p.Minutes), target), StringComparer.Ordinal);

    public static decimal? Minutes(double? value) => Figures.Round1(value);
}

/// <summary>LIS-07: laboratory volume and turnaround time by section or test.</summary>
internal sealed class LabVolumeTatReport : ReportDefinition
{
    private const string BySection = "section";
    private const string ByTest = "test";

    public override ReportDescriptor Descriptor { get; } = new(
        "lab-volume-tat",
        "Laboratory volume and turnaround",
        ReportCategory.Diagnostics,
        "Tests ordered, released and pending by lab section or test, with median and 90th-percentile turnaround times.",
        "Tests ordered in the period (by accession time). Total TAT = order to report release; in-lab TAT = sample received in the lab to release. Median and 90th percentile use the nearest-rank method. Within target = released tests whose total TAT was at or under the target.",
        Audience.Operations,
        [
            P.Period(ReportPeriods.Last30Days),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(BySection, "Lab section"), new(ByTest, "Test")], BySection),
            P.Choice(ReportKeys.Section, "Lab section", Choices.Of<LabSection>()),
            Turnaround.TargetParameter(240),
        ],
        [
            C.Text("key", "Section"),
            C.Int("ordered", "Ordered"),
            C.Int("released", "Released"),
            C.Int("pending", "Pending"),
            C.Int("cancelled", "Cancelled"),
            C.Int("stat", "STAT"),
            C.Minutes("medianTat", "Median TAT"),
            C.Minutes("p90Tat", "90th percentile TAT"),
            C.Minutes("medianLabTat", "Median in-lab TAT"),
            C.Pct("withinTarget", "Within target %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "key", ["medianTat", "p90Tat"]),
        DefaultSort: "ordered",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var byTest = ctx.Args.Text(ReportKeys.GroupBy) == ByTest;
        var section = ctx.Args.EnumOf<LabSection>(ReportKeys.Section);
        var target = ctx.Args.NumberOf(Turnaround.TargetKey) ?? 240;

        var items =
            from i in ctx.Db.Set<LabOrderItem>().AsNoTracking()
            join o in ctx.Db.Set<LabOrder>() on i.LabOrderId equals o.Id
            where o.OrderedAt >= start && o.OrderedAt < end && (section == null || i.Section == section)
            select new { i.Section, i.TestCode, i.TestName, i.Status, i.Priority, i.ReleasedAt, o.OrderedAt, i.SampleId };
        var released = items.Where(x => x.Status == LabOrderItemStatus.Released && x.ReleasedAt != null);
        var withSample =
            from x in released
            join s in ctx.Db.Set<LabSample>() on x.SampleId equals (Guid?)s.Id
            where s.ReceivedAt != null
            select new { x.Section, x.TestCode, x.ReleasedAt, s.ReceivedAt };

        List<Volume> volumes;
        List<KeyMinutes> total, inLab;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (byTest)
        {
            var rows = await ctx.ListAsync(
                items.GroupBy(x => x.TestCode)
                    .Select(g => new TestVolume(
                        g.Key,
                        g.Max(x => x.TestName)!,
                        g.Count(),
                        g.Count(x => x.Status == LabOrderItemStatus.Released),
                        g.Count(x => x.Status == LabOrderItemStatus.Cancelled),
                        g.Count(x => x.Priority == OrderPriority.Stat && x.Status != LabOrderItemStatus.Cancelled))),
                ct);
            volumes = rows.Select(r => new Volume(r.Code, r.Ordered, r.Released, r.Cancelled, r.Stat)).ToList();
            foreach (var r in rows)
            {
                names[r.Code] = $"{r.Name} ({r.Code})";
            }

            total = await ctx.MeasurementsAsync(released.Select(x => new KeyMinutes(x.TestCode, (x.ReleasedAt!.Value - x.OrderedAt).TotalMinutes)), ct);
            inLab = await ctx.MeasurementsAsync(withSample.Select(x => new KeyMinutes(x.TestCode, (x.ReleasedAt!.Value - x.ReceivedAt!.Value).TotalMinutes)), ct);
        }
        else
        {
            var rows = await ctx.ListAsync(
                items.GroupBy(x => x.Section)
                    .Select(g => new SectionVolume(
                        g.Key,
                        g.Count(),
                        g.Count(x => x.Status == LabOrderItemStatus.Released),
                        g.Count(x => x.Status == LabOrderItemStatus.Cancelled),
                        g.Count(x => x.Priority == OrderPriority.Stat && x.Status != LabOrderItemStatus.Cancelled))),
                ct);
            volumes = rows.Select(r => new Volume(r.Section.ToString(), r.Ordered, r.Released, r.Cancelled, r.Stat)).ToList();
            foreach (var r in rows)
            {
                names[r.Section.ToString()] = Labels.Humanize(r.Section.ToString());
            }

            var totalBySection = await ctx.MeasurementsAsync(released.Select(x => new SectionMinutes(x.Section, (x.ReleasedAt!.Value - x.OrderedAt).TotalMinutes)), ct);
            var inLabBySection = await ctx.MeasurementsAsync(withSample.Select(x => new SectionMinutes(x.Section, (x.ReleasedAt!.Value - x.ReceivedAt!.Value).TotalMinutes)), ct);
            total = totalBySection.Select(m => new KeyMinutes(m.Section.ToString(), m.Minutes)).ToList();
            inLab = inLabBySection.Select(m => new KeyMinutes(m.Section.ToString(), m.Minutes)).ToList();
        }

        var totalTat = Turnaround.ByKey(total, target);
        var labTat = Turnaround.ByKey(inLab, null);
        var result = volumes
            .Select(v =>
            {
                var tat = totalTat.GetValueOrDefault(v.Key);
                var lab = labTat.GetValueOrDefault(v.Key);
                return new object?[]
                {
                    names.GetValueOrDefault(v.Key, v.Key), v.Ordered, v.Released, v.Ordered - v.Released - v.Cancelled, v.Cancelled, v.Stat,
                    Turnaround.Minutes(tat?.MedianMinutes), Turnaround.Minutes(tat?.P90Minutes), Turnaround.Minutes(lab?.MedianMinutes),
                    Figures.Round1(tat?.WithinTargetPercent),
                };
            })
            .ToList();

        var overall = Percentiles.Summarize(total.Select(t => t.Minutes), target);
        var overallLab = Percentiles.Summarize(inLab.Select(t => t.Minutes));
        var ordered = volumes.Sum(v => v.Ordered);
        var releasedCount = volumes.Sum(v => v.Released);
        var cancelled = volumes.Sum(v => v.Cancelled);
        return new ReportData(result)
        {
            Columns = byTest ? [C.Text("key", "Test"), .. Descriptor.Columns.Skip(1)] : null,
            RowKeys = byTest ? null : volumes.Select(v => (string?)v.Key).ToList(),
            DrillDown = byTest
                ? null
                : new ReportDrillDown("lab-volume-tat", ReportKeys.Section, "See tests", new Dictionary<string, string> { [ReportKeys.GroupBy] = ByTest }),
            Totals =
            [
                "All tests", ordered, releasedCount, ordered - releasedCount - cancelled, cancelled, volumes.Sum(v => v.Stat), Turnaround.Minutes(overall.MedianMinutes),
                Turnaround.Minutes(overall.P90Minutes), Turnaround.Minutes(overallLab.MedianMinutes), Figures.Round1(overall.WithinTargetPercent),
            ],
            Summary =
            [
                new("ordered", "Tests ordered", ordered, ReportValueType.Integer),
                new("medianTat", "Median TAT", Turnaround.Minutes(overall.MedianMinutes), ReportValueType.Minutes),
                new("p90Tat", "90th percentile TAT", Turnaround.Minutes(overall.P90Minutes), ReportValueType.Minutes),
                new("withinTarget", string.Create(CultureInfo.InvariantCulture, $"Within {target} min"), Figures.Round1(overall.WithinTargetPercent), ReportValueType.Percent),
            ],
        };
    }

    private sealed record Volume(string Key, int Ordered, int Released, int Cancelled, int Stat);

    private sealed record TestVolume(string Code, string Name, int Ordered, int Released, int Cancelled, int Stat);

    private sealed record SectionVolume(LabSection Section, int Ordered, int Released, int Cancelled, int Stat);

    private sealed record SectionMinutes(LabSection Section, double Minutes);
}

/// <summary>IMG-04: radiology volume and turnaround by modality or study.</summary>
internal sealed class RadiologyVolumeTatReport : ReportDefinition
{
    private const string ByModality = "modality";
    private const string ByStudy = "study";

    public override ReportDescriptor Descriptor { get; } = new(
        "radiology-volume-tat",
        "Radiology volume and turnaround",
        ReportCategory.Diagnostics,
        "Imaging studies ordered and reported by modality or study, with order-to-report and reporting turnaround.",
        "Studies ordered in the period. Reported = reported, verified or released. TAT = order to first report; reporting time = images acquired to report. Median and 90th percentile use the nearest-rank method.",
        Audience.Operations,
        [
            P.Period(ReportPeriods.Last30Days),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByModality, "Modality"), new(ByStudy, "Study")], ByModality),
            P.Choice(ReportKeys.Modality, "Modality", Choices.Of<Modality>(m => m.ToString())),
            Turnaround.TargetParameter(1440),
        ],
        [
            C.Text("key", "Modality"),
            C.Int("ordered", "Ordered"),
            C.Int("reported", "Reported"),
            C.Int("pending", "Pending"),
            C.Int("cancelled", "Cancelled"),
            C.Int("stat", "STAT"),
            C.Minutes("medianTat", "Median TAT"),
            C.Minutes("p90Tat", "90th percentile TAT"),
            C.Minutes("medianReporting", "Median reporting time"),
            C.Pct("withinTarget", "Within target %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "key", ["ordered"]),
        DefaultSort: "ordered",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var byStudy = ctx.Args.Text(ReportKeys.GroupBy) == ByStudy;
        var modality = ctx.Args.EnumOf<Modality>(ReportKeys.Modality);
        var target = ctx.Args.NumberOf(Turnaround.TargetKey) ?? 1440;
        var studies = ctx.Db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.OrderedAt >= start && s.OrderedAt < end && (modality == null || s.Modality == modality));
        var reported = studies.Where(s => s.ReportedAt != null);

        List<Volume> volumes;
        List<KeyMinutes> tat, reporting;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (byStudy)
        {
            var rows = await ctx.ListAsync(
                studies.GroupBy(s => s.ServiceCode)
                    .Select(g => new StudyVolume(
                        g.Key,
                        g.Max(s => s.StudyName)!,
                        g.Count(),
                        g.Count(s => s.Status == ImagingStudyStatus.Reported || s.Status == ImagingStudyStatus.Verified || s.Status == ImagingStudyStatus.Released),
                        g.Count(s => s.Status == ImagingStudyStatus.Cancelled),
                        g.Count(s => s.Priority == OrderPriority.Stat && s.Status != ImagingStudyStatus.Cancelled))),
                ct);
            volumes = rows.Select(r => new Volume(r.Code, r.Ordered, r.Reported, r.Cancelled, r.Stat)).ToList();
            foreach (var r in rows)
            {
                names[r.Code] = r.Name;
            }

            tat = await ctx.MeasurementsAsync(reported.Select(s => new KeyMinutes(s.ServiceCode, (s.ReportedAt!.Value - s.OrderedAt).TotalMinutes)), ct);
            reporting = await ctx.MeasurementsAsync(
                reported.Where(s => s.AcquiredAt != null).Select(s => new KeyMinutes(s.ServiceCode, (s.ReportedAt!.Value - s.AcquiredAt!.Value).TotalMinutes)), ct);
        }
        else
        {
            var rows = await ctx.ListAsync(
                studies.GroupBy(s => s.Modality)
                    .Select(g => new ModalityVolume(
                        g.Key,
                        g.Count(),
                        g.Count(s => s.Status == ImagingStudyStatus.Reported || s.Status == ImagingStudyStatus.Verified || s.Status == ImagingStudyStatus.Released),
                        g.Count(s => s.Status == ImagingStudyStatus.Cancelled),
                        g.Count(s => s.Priority == OrderPriority.Stat && s.Status != ImagingStudyStatus.Cancelled))),
                ct);
            volumes = rows.Select(r => new Volume(r.Modality.ToString(), r.Ordered, r.Reported, r.Cancelled, r.Stat)).ToList();
            foreach (var r in rows)
            {
                names[r.Modality.ToString()] = ModalityName(r.Modality);
            }

            var tatRows = await ctx.MeasurementsAsync(reported.Select(s => new ModalityMinutes(s.Modality, (s.ReportedAt!.Value - s.OrderedAt).TotalMinutes)), ct);
            var reportingRows = await ctx.MeasurementsAsync(
                reported.Where(s => s.AcquiredAt != null).Select(s => new ModalityMinutes(s.Modality, (s.ReportedAt!.Value - s.AcquiredAt!.Value).TotalMinutes)), ct);
            tat = tatRows.Select(m => new KeyMinutes(m.Modality.ToString(), m.Minutes)).ToList();
            reporting = reportingRows.Select(m => new KeyMinutes(m.Modality.ToString(), m.Minutes)).ToList();
        }

        var tatByKey = Turnaround.ByKey(tat, target);
        var reportingByKey = Turnaround.ByKey(reporting, null);
        var result = volumes
            .Select(v =>
            {
                var t = tatByKey.GetValueOrDefault(v.Key);
                var r = reportingByKey.GetValueOrDefault(v.Key);
                return new object?[]
                {
                    names.GetValueOrDefault(v.Key, v.Key), v.Ordered, v.Reported, Math.Max(0, v.Ordered - v.Reported - v.Cancelled), v.Cancelled, v.Stat,
                    Turnaround.Minutes(t?.MedianMinutes), Turnaround.Minutes(t?.P90Minutes), Turnaround.Minutes(r?.MedianMinutes), Figures.Round1(t?.WithinTargetPercent),
                };
            })
            .ToList();

        var overall = Percentiles.Summarize(tat.Select(t => t.Minutes), target);
        var overallReporting = Percentiles.Summarize(reporting.Select(t => t.Minutes));
        var ordered = volumes.Sum(v => v.Ordered);
        var reportedCount = volumes.Sum(v => v.Reported);
        var cancelled = volumes.Sum(v => v.Cancelled);
        return new ReportData(result)
        {
            Columns = byStudy ? [C.Text("key", "Study"), .. Descriptor.Columns.Skip(1)] : null,
            RowKeys = byStudy ? null : volumes.Select(v => (string?)v.Key).ToList(),
            DrillDown = byStudy
                ? null
                : new ReportDrillDown("radiology-volume-tat", ReportKeys.Modality, "See studies", new Dictionary<string, string> { [ReportKeys.GroupBy] = ByStudy }),
            Totals =
            [
                "All studies", ordered, reportedCount, Math.Max(0, ordered - reportedCount - cancelled), cancelled, volumes.Sum(v => v.Stat),
                Turnaround.Minutes(overall.MedianMinutes), Turnaround.Minutes(overall.P90Minutes), Turnaround.Minutes(overallReporting.MedianMinutes),
                Figures.Round1(overall.WithinTargetPercent),
            ],
            Summary =
            [
                new("ordered", "Studies ordered", ordered, ReportValueType.Integer),
                new("medianTat", "Median TAT", Turnaround.Minutes(overall.MedianMinutes), ReportValueType.Minutes),
                new("medianReporting", "Median reporting time", Turnaround.Minutes(overallReporting.MedianMinutes), ReportValueType.Minutes),
            ],
        };
    }

    private static string ModalityName(Modality modality) => modality switch
    {
        Modality.XR => "X-ray (XR)",
        Modality.CT => "CT scan",
        Modality.MR => "MRI",
        Modality.US => "Ultrasound",
        Modality.MG => "Mammography",
        Modality.RF => "Fluoroscopy",
        Modality.DX => "Digital X-ray (DX)",
        _ => modality.ToString(),
    };

    private sealed record Volume(string Key, int Ordered, int Reported, int Cancelled, int Stat);

    private sealed record StudyVolume(string Code, string Name, int Ordered, int Reported, int Cancelled, int Stat);

    private sealed record ModalityVolume(Modality Modality, int Ordered, int Reported, int Cancelled, int Stat);

    private sealed record ModalityMinutes(Modality Modality, double Minutes);
}

/// <summary>
/// LIS-05 / NABH AAC.7: critical results and how fast they were communicated (patient-level; requires
/// analytics.clinical.read, every view and export is audited per patient).
/// </summary>
internal sealed class CriticalValuesReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "critical-values",
        "Critical values register",
        ReportCategory.Diagnostics,
        "Critical lab results and critical imaging or cardiology findings, who was informed and how quickly.",
        "Lab results flagged critical in the period (by result entry time) with their critical-call record, plus documented critical imaging and cardiology calls. Minutes to inform = result entry to the documented call. Lists patient identifiers: opening or exporting it is recorded in the audit trail for each patient.",
        Audience.Clinical,
        [P.Period(ReportPeriods.Last7Days), P.Facility()],
        [
            C.DateTime("flaggedAt", "Flagged"),
            C.Patient("uhid", "UHID"),
            C.Patient("patient", "Patient"),
            C.Text("kind", "Service"),
            C.Text("test", "Test / study"),
            C.Text("result", "Result / finding"),
            C.DateTime("informedAt", "Informed at"),
            C.Minutes("minutes", "Minutes to inform"),
            C.Text("informed", "Informed"),
            C.Text("method", "How"),
            C.Text("readBack", "Read back"),
        ],
        null,
        DefaultSort: "flaggedAt",
        DefaultSortDescending: true,
        ContainsPatientIdentifiers: true,
        MaxDays: 93);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var patients = ctx.Db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]);
        var lab = await ctx.ListAsync(
            from r in ctx.Db.Set<LabResult>().AsNoTracking()
            join p in patients on r.PatientId equals p.Id
            join c in ctx.Db.Set<CriticalCallLog>() on (Guid?)r.Id equals c.LabResultId into calls
            from c in calls.DefaultIfEmpty()
            where (r.Flag == ResultFlag.CriticalLow || r.Flag == ResultFlag.CriticalHigh || r.Flag == ResultFlag.Critical)
                  && r.EnteredAt >= start && r.EnteredAt < end
            orderby r.EnteredAt descending
            select new CriticalRow(
                r.PatientId, p.Uhid, p.FullName, DiagnosticKind.Laboratory, r.TestName, r.NumericValue, r.ValueText, r.Unit, r.Flag, r.EnteredAt!.Value,
                c == null ? (DateTimeOffset?)null : c.InformedAt, c == null ? null : c.InformedPersonName, c == null ? (CommunicationMethod?)null : c.Method,
                c != null && c.ReadBackConfirmed),
            ct);
        var findings = await ctx.ListAsync(
            from c in ctx.Db.Set<CriticalCallLog>().AsNoTracking()
            join p in patients on c.PatientId equals p.Id
            where c.Kind != DiagnosticKind.Laboratory && c.InformedAt >= start && c.InformedAt < end
            orderby c.InformedAt descending
            select new CriticalRow(
                c.PatientId, p.Uhid, p.FullName, c.Kind, c.Finding, null, null, null, null, c.CreatedAt, c.InformedAt, c.InformedPersonName, c.Method,
                c.ReadBackConfirmed),
            ct);

        var all = lab.Concat(findings).OrderByDescending(r => r.FlaggedAt).ToList();
        var rows = all
            .Select(r =>
            {
                double? minutes = r.InformedAt is { } informed && informed >= r.FlaggedAt ? (informed - r.FlaggedAt).TotalMinutes : null;
                return new object?[]
                {
                    r.FlaggedAt, r.Uhid, r.PatientName, Labels.Humanize(r.Kind.ToString()), r.Test, ResultText(r), r.InformedAt, Figures.Round1(minutes),
                    r.InformedPerson ?? "Not documented", r.Method is { } method ? Labels.Humanize(method.ToString()) : null, r.InformedAt is null ? null : r.ReadBack ? "Yes" : "No",
                };
            })
            .ToList();

        var informedMinutes = all
            .Where(r => r.InformedAt is { } informed && informed >= r.FlaggedAt)
            .Select(r => (r.InformedAt!.Value - r.FlaggedAt).TotalMinutes)
            .ToList();
        var summary = Percentiles.Summarize(informedMinutes, 30);
        return new ReportData(rows)
        {
            Summary =
            [
                new("critical", "Critical results", all.Count, ReportValueType.Integer),
                new("informedPercent", "Documented calls", KpiMath.Percent(all.Count(r => r.InformedAt is not null), all.Count), ReportValueType.Percent),
                new("medianMinutes", "Median minutes to inform", Figures.Round1(summary.MedianMinutes), ReportValueType.Minutes),
                new("within30", "Informed within 30 min", Figures.Round1(summary.WithinTargetPercent), ReportValueType.Percent),
            ],
            RowPatientIds = all.Select(r => (Guid?)r.PatientId).ToList(),
        };
    }

    private static string ResultText(CriticalRow r)
    {
        var value = r.NumericValue is { } number
            ? number.ToString("0.####", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(r.Unit) ? string.Empty : " " + r.Unit)
            : r.ValueText ?? string.Empty;
        return r.Flag is { } flag ? $"{value} ({Labels.Humanize(flag.ToString()).ToLowerInvariant()})".Trim() : value;
    }

    private sealed record CriticalRow(
        Guid PatientId, string Uhid, string PatientName, DiagnosticKind Kind, string Test, decimal? NumericValue, string? ValueText, string? Unit, ResultFlag? Flag,
        DateTimeOffset FlaggedAt, DateTimeOffset? InformedAt, string? InformedPerson, CommunicationMethod? Method, bool ReadBack);
}
