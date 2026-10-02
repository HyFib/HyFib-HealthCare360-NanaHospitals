using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Analytics.Seed;

/// <summary>
/// Reference data (idempotent, never re-creates rows that were deleted on purpose): the e-mail template used by
/// scheduled reports, and two shared starter dashboards ("Hospital operations" and "Finance overview") that analysts
/// can copy and adapt.
/// </summary>
internal sealed class AnalyticsSeeder : ISeeder
{
    public const string OperationsDashboard = "Hospital operations";
    public const string FinanceDashboard = "Finance overview";

    public int Order => 190;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var templateExists = await db.Set<NotificationTemplate>().IgnoreQueryFilters()
            .AnyAsync(t => t.Key == ScheduledReportEmailHandler.TemplateKey && t.Channel == NotificationChannel.Email && t.FacilityId == null, cancellationToken);
        if (!templateExists)
        {
            db.Add(new NotificationTemplate
            {
                Key = ScheduledReportEmailHandler.TemplateKey,
                Channel = NotificationChannel.Email,
                Language = "en",
                Subject = "{{report}} — {{period}} ({{hospital}})",
                Body = "<p>Hello,</p>"
                       + "<p>Please find attached the <strong>{{report}}</strong> report for <strong>{{period}}</strong> from {{hospital}}.</p>"
                       + "<p>Scheduled e-mail: {{schedule}}<br>Generated: {{generatedAt}}<br>Rows: {{rows}}</p>"
                       + "<p>This report is confidential and meant only for the people it was sent to. Please do not forward it outside the hospital.</p>"
                       + "<p>— HyFib HealthCare360 (automatic e-mail; replies are not read)</p>",
                Description = "Scheduled MIS report e-mail (the Excel, CSV or PDF file is attached)",
            });
        }

        var existing = await db.Set<Dashboard>().IgnoreQueryFilters()
            .Where(d => d.OwnerUserId == null)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);
        if (!existing.Contains(OperationsDashboard))
        {
            db.Add(Template(OperationsDashboard, "Out-patient load, beds, admissions, lab turnaround and patient feedback at a glance.",
            [
                Widget("opd", "op-visit-trend", "OP visits — last 30 days", ReportChartType.StackedBar, 2, ReportPeriods.Last30Days),
                Widget("beds", "bed-occupancy", "Bed occupancy this month", ReportChartType.Bar, 2, ReportPeriods.ThisMonth),
                Widget("adt", "ip-admissions-discharges", "Admissions and discharges", ReportChartType.Line, 2, ReportPeriods.Last30Days),
                Widget("lab", "lab-volume-tat", "Lab turnaround by section", ReportChartType.Bar, 2, ReportPeriods.Last30Days),
                Widget("outcomes", "discharge-outcomes", "Discharge outcomes", ReportChartType.Summary, 2, ReportPeriods.ThisMonth),
                Widget("nps", "feedback-nps", "Patient feedback", ReportChartType.Summary, 2, ReportPeriods.ThisMonth),
            ]));
        }

        if (!existing.Contains(FinanceDashboard))
        {
            db.Add(Template(FinanceDashboard, "Revenue, collections, payers and dues for accounts and management.",
            [
                Widget("trend", "daily-revenue", "Revenue this month", ReportChartType.StackedBar, 4, ReportPeriods.ThisMonth),
                Widget("departments", "revenue-by-department", "Revenue by department", ReportChartType.Bar, 2, ReportPeriods.ThisMonth),
                Widget("modes", "collections-by-mode", "Collections by mode", ReportChartType.Pie, 2, ReportPeriods.ThisMonth),
                Widget("payers", "revenue-by-payer", "Revenue by payer", ReportChartType.Bar, 2, ReportPeriods.ThisMonth),
                Widget("ageing", "outstanding-ageing", "Outstanding dues", ReportChartType.StackedBar, 2, null),
            ]));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Dashboard Template(string name, string description, IReadOnlyList<DashboardWidget> widgets) => new()
    {
        Name = name,
        Description = description,
        IsShared = true,
        OwnerUserId = null,
        OwnerName = "HC360",
        LayoutJson = DashboardLayout.Serialize(widgets),
    };

    private static DashboardWidget Widget(string id, string reportId, string title, ReportChartType view, int width, string? period) =>
        new(id, reportId, title, view, width, period is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [ReportKeys.Period] = period });
}
