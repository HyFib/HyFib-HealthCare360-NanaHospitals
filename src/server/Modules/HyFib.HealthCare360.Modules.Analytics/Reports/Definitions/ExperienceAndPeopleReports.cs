using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>FB-04: Net Promoter Score and ratings from patient feedback.</summary>
internal sealed class FeedbackNpsReport : ReportDefinition
{
    private const string ByDepartment = "department";
    private const string ByChannel = "channel";
    private const string ByForm = "form";

    public override ReportDescriptor Descriptor { get; } = new(
        "feedback-nps",
        "Patient feedback and NPS",
        ReportCategory.Experience,
        "Net Promoter Score, average rating and low scores from patient feedback by department, channel or form.",
        "Responses submitted in the period. NPS = % promoters (9–10) − % detractors (0–6) among responses that answered the recommend question. Average rating = mean overall rating (1–5). Low scores = responses flagged for follow-up (any rating of 2 or less, or NPS 6 or less).",
        Audience.Operations,
        [
            P.Period(),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByDepartment, "Department"), new(ByChannel, "How it was given"), new(ByForm, "Form (OP / IP / portal)")], ByDepartment),
            P.Department(),
        ],
        [
            C.Text("key", "Department"),
            C.Int("responses", "Responses"),
            C.Int("promoters", "Promoters"),
            C.Int("passives", "Passives"),
            C.Int("detractors", "Detractors"),
            C.Dec("nps", "NPS"),
            C.Dec("rating", "Average rating"),
            C.Int("lowScores", "Low scores"),
        ],
        new ReportChartHint(ReportChartType.Bar, "key", ["nps"]),
        DefaultSort: "responses",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var departmentId = ctx.Args.DepartmentId;
        var groupBy = ctx.Args.Text(ReportKeys.GroupBy) ?? ByDepartment;
        var responses =
            from r in ctx.Db.Set<FeedbackResponse>().AsNoTracking()
            join f in ctx.Db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on r.FormId equals f.Id
            where r.SubmittedAt >= start && r.SubmittedAt < end && (departmentId == null || r.DepartmentId == departmentId)
            select new { r.DepartmentId, r.Channel, FormType = f.Type, r.Nps, r.OverallRating, r.IsLowScore };

        List<NpsRow> groups = groupBy switch
        {
            ByChannel => await ctx.ListAsync(
                responses.GroupBy(r => r.Channel).Select(g => new NpsRow(
                    null, g.Key, null, g.Count(), g.Count(r => r.Nps >= 9), g.Count(r => r.Nps >= 7 && r.Nps <= 8), g.Count(r => r.Nps <= 6),
                    g.Average(r => (double?)r.OverallRating), g.Count(r => r.IsLowScore))),
                ct),
            ByForm => await ctx.ListAsync(
                responses.GroupBy(r => r.FormType).Select(g => new NpsRow(
                    null, null, g.Key, g.Count(), g.Count(r => r.Nps >= 9), g.Count(r => r.Nps >= 7 && r.Nps <= 8), g.Count(r => r.Nps <= 6),
                    g.Average(r => (double?)r.OverallRating), g.Count(r => r.IsLowScore))),
                ct),
            _ => await ctx.ListAsync(
                responses.GroupBy(r => r.DepartmentId).Select(g => new NpsRow(
                    g.Key, null, null, g.Count(), g.Count(r => r.Nps >= 9), g.Count(r => r.Nps >= 7 && r.Nps <= 8), g.Count(r => r.Nps <= 6),
                    g.Average(r => (double?)r.OverallRating), g.Count(r => r.IsLowScore))),
                ct),
        };

        var departments = await Names.DepartmentsAsync(ctx, groups.Select(g => g.DepartmentId), ct);
        string Label(NpsRow g) => groupBy switch
        {
            ByChannel => Labels.Humanize(g.Channel?.ToString() ?? Labels.NotRecorded),
            ByForm => g.Form switch
            {
                FeedbackFormType.OP => "Out-patient",
                FeedbackFormType.IP => "In-patient",
                FeedbackFormType.Portal => "Patient portal",
                FeedbackFormType.General => "General (QR posters)",
                _ => Labels.NotRecorded,
            },
            _ => Labels.Name(departments, g.DepartmentId, "Whole hospital"),
        };

        var rows = groups
            .Select(g => new object?[]
            {
                Label(g), g.Responses, g.Promoters, g.Passives, g.Detractors, KpiMath.NetPromoterScore(g.Promoters, g.Passives, g.Detractors),
                Figures.Round1(g.AverageRating), g.LowScores,
            })
            .ToList();

        var promoters = groups.Sum(g => g.Promoters);
        var passives = groups.Sum(g => g.Passives);
        var detractors = groups.Sum(g => g.Detractors);
        var responsesTotal = groups.Sum(g => g.Responses);
        var ratings = groups.Where(g => g.AverageRating is not null).ToList();
        decimal? rating = ratings.Count == 0 ? null : Figures.Round1(ratings.Sum(g => g.AverageRating!.Value * g.Responses) / Math.Max(1, ratings.Sum(g => g.Responses)));
        return new ReportData(rows)
        {
            Columns = groupBy switch
            {
                ByChannel => [C.Text("key", "How it was given"), .. Descriptor.Columns.Skip(1)],
                ByForm => [C.Text("key", "Form"), .. Descriptor.Columns.Skip(1)],
                _ => null,
            },
            Totals = ["All responses", responsesTotal, promoters, passives, detractors, KpiMath.NetPromoterScore(promoters, passives, detractors), rating, groups.Sum(g => g.LowScores)],
            Summary =
            [
                new("nps", "NPS", KpiMath.NetPromoterScore(promoters, passives, detractors), ReportValueType.Decimal),
                new("responses", "Responses", responsesTotal, ReportValueType.Integer),
                new("rating", "Average rating (of 5)", rating, ReportValueType.Decimal),
                new("lowScores", "Low scores", groups.Sum(g => g.LowScores), ReportValueType.Integer),
            ],
        };
    }

    private sealed record NpsRow(
        Guid? DepartmentId, FeedbackChannel? Channel, FeedbackFormType? Form, int Responses, int Promoters, int Passives, int Detractors, double? AverageRating,
        int LowScores);
}

/// <summary>HR-01: head count by department and cadre, joiners, leavers and attrition.</summary>
internal sealed class HrHeadcountReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "hr-headcount",
        "Head count and attrition",
        ReportCategory.People,
        "Current staff by department and cadre (medical, nursing, paramedical, administrative, support), with joiners and leavers in the period.",
        "Head count = employees not separated who have joined by today. Cadre comes from the designation. Attrition % = leavers in the period ÷ head count × 100.",
        Audience.Mis,
        [P.Period(ReportPeriods.ThisFinancialYear), P.Facility(), P.Department()],
        [
            C.Text("department", "Department"),
            C.Int("headcount", "Head count"),
            C.Int("medical", "Medical"),
            C.Int("nursing", "Nursing"),
            C.Int("paramedical", "Paramedical"),
            C.Int("administrative", "Administrative"),
            C.Int("support", "Support"),
            C.Int("permanent", "Permanent"),
            C.Int("contract", "Contract / other"),
            C.Int("joiners", "Joined"),
            C.Int("leavers", "Left"),
            C.Pct("attrition", "Attrition %"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "department", ["medical", "nursing", "paramedical", "administrative", "support"]),
        DefaultSort: "headcount",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (periodFrom, periodTo) = (ctx.Args.From, ctx.Args.To);
        var today = ctx.Today;
        var departmentId = ctx.Args.DepartmentId;
        var staff =
            from e in ctx.Db.Set<Employee>().AsNoTracking()
            join d in ctx.Db.Set<Designation>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on e.DesignationId equals (Guid?)d.Id into designations
            from d in designations.DefaultIfEmpty()
            where departmentId == null || e.DepartmentId == departmentId
            select new
            {
                e.DepartmentId,
                Current = e.Status != EmployeeStatus.Separated && e.JoiningDate <= today,
                Category = d == null ? (StaffCategory?)null : d.Category,
                Permanent = e.EmploymentType == EmploymentType.Permanent,
                Joined = e.JoiningDate >= periodFrom && e.JoiningDate <= periodTo,
                Left = e.Status == EmployeeStatus.Separated && e.LeavingDate >= periodFrom && e.LeavingDate <= periodTo,
            };
        var groups = await ctx.ListAsync(
            staff.GroupBy(s => s.DepartmentId)
                .Select(g => new HeadcountRow(
                    g.Key,
                    g.Count(s => s.Current),
                    g.Count(s => s.Current && s.Category == StaffCategory.Medical),
                    g.Count(s => s.Current && s.Category == StaffCategory.Nursing),
                    g.Count(s => s.Current && s.Category == StaffCategory.Paramedical),
                    g.Count(s => s.Current && s.Category == StaffCategory.Administrative),
                    g.Count(s => s.Current && s.Category == StaffCategory.Support),
                    g.Count(s => s.Current && s.Permanent),
                    g.Count(s => s.Joined),
                    g.Count(s => s.Left))),
            ct);

        var names = await Names.DepartmentsAsync(ctx, groups.Select(g => g.DepartmentId), ct);
        var rows = groups
            .Where(g => g.Headcount + g.Joined + g.Left > 0)
            .Select(g => Row(Labels.Name(names, g.DepartmentId, "No department"), g))
            .ToList();
        var total = new HeadcountRow(
            null, groups.Sum(g => g.Headcount), groups.Sum(g => g.Medical), groups.Sum(g => g.Nursing), groups.Sum(g => g.Paramedical), groups.Sum(g => g.Administrative),
            groups.Sum(g => g.Support), groups.Sum(g => g.Permanent), groups.Sum(g => g.Joined), groups.Sum(g => g.Left));
        return new ReportData(rows)
        {
            Totals = Row("Total", total),
            Summary =
            [
                new("headcount", "Head count", total.Headcount, ReportValueType.Integer),
                new("joiners", "Joined", total.Joined, ReportValueType.Integer),
                new("leavers", "Left", total.Left, ReportValueType.Integer),
                new("attrition", "Attrition", KpiMath.Percent(total.Left, total.Headcount), ReportValueType.Percent),
            ],
        };
    }

    private static object?[] Row(string label, HeadcountRow g) =>
    [
        label, g.Headcount, g.Medical, g.Nursing, g.Paramedical, g.Administrative, g.Support, g.Permanent, g.Headcount - g.Permanent, g.Joined, g.Left,
        KpiMath.Percent(g.Left, g.Headcount),
    ];

    private sealed record HeadcountRow(
        Guid? DepartmentId, int Headcount, int Medical, int Nursing, int Paramedical, int Administrative, int Support, int Permanent, int Joined, int Left);
}

/// <summary>HR-04: attendance, late arrivals and overtime by department.</summary>
internal sealed class HrAttendanceReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "hr-attendance",
        "Attendance and overtime",
        ReportCategory.People,
        "Present, absent and leave days, late arrivals and overtime hours by department from the computed daily attendance.",
        "From the nightly attendance computation for dates in the period. Attendance % = present days ÷ (present + absent + leave days); week-offs and holidays are excluded. Overtime hours = overtime minutes ÷ 60.",
        Audience.Mis,
        [P.Period(ReportPeriods.LastMonth), P.Facility(), P.Department()],
        [
            C.Text("department", "Department"),
            C.Int("employees", "Employees"),
            C.Dec("present", "Present days"),
            C.Dec("absent", "Absent days"),
            C.Dec("leave", "Leave days"),
            C.Pct("attendance", "Attendance %"),
            C.Int("late", "Late arrivals"),
            C.Dec("overtime", "Overtime hours"),
        ],
        new ReportChartHint(ReportChartType.Bar, "department", ["attendance"]),
        DefaultSort: "employees",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (periodFrom, periodTo) = (ctx.Args.From, ctx.Args.To);
        var departmentId = ctx.Args.DepartmentId;
        var groups = await ctx.ListAsync(
            (from a in ctx.Db.Set<AttendanceDay>().AsNoTracking()
             join e in ctx.Db.Set<Employee>() on a.EmployeeId equals e.Id
             where a.Date >= periodFrom && a.Date <= periodTo && (departmentId == null || e.DepartmentId == departmentId)
             select new { e.DepartmentId, a.EmployeeId, a.PresentDays, a.AbsentDays, Leave = a.PaidLeaveDays + a.UnpaidLeaveDays, a.LateMinutes, a.OvertimeMinutes })
            .GroupBy(x => x.DepartmentId)
            .Select(g => new AttendanceRow(
                g.Key,
                g.Select(x => x.EmployeeId).Distinct().Count(),
                g.Sum(x => x.PresentDays),
                g.Sum(x => x.AbsentDays),
                g.Sum(x => x.Leave),
                g.Count(x => x.LateMinutes > 0),
                g.Sum(x => x.OvertimeMinutes))),
            ct);

        var names = await Names.DepartmentsAsync(ctx, groups.Select(g => g.DepartmentId), ct);
        var rows = groups.Select(g => Row(Labels.Name(names, g.DepartmentId, "No department"), g)).ToList();
        var total = new AttendanceRow(
            null, groups.Sum(g => g.Employees), groups.Sum(g => g.Present), groups.Sum(g => g.Absent), groups.Sum(g => g.Leave), groups.Sum(g => g.Late),
            groups.Sum(g => g.OvertimeMinutes));
        return new ReportData(rows)
        {
            Totals = Row("Total", total),
            Summary =
            [
                new("attendance", "Attendance", KpiMath.Percent(total.Present, total.Present + total.Absent + total.Leave), ReportValueType.Percent),
                new("late", "Late arrivals", total.Late, ReportValueType.Integer),
                new("overtime", "Overtime hours", Math.Round(total.OvertimeMinutes / 60m, 1, MidpointRounding.AwayFromZero), ReportValueType.Decimal),
            ],
        };
    }

    private static object?[] Row(string label, AttendanceRow g) =>
    [
        label, g.Employees, g.Present, g.Absent, g.Leave, KpiMath.Percent(g.Present, g.Present + g.Absent + g.Leave), g.Late,
        Math.Round(g.OvertimeMinutes / 60m, 1, MidpointRounding.AwayFromZero),
    ];

    private sealed record AttendanceRow(Guid? DepartmentId, int Employees, decimal Present, decimal Absent, decimal Leave, int Late, int OvertimeMinutes);
}
