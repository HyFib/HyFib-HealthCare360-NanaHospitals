using HyFib.HealthCare360.Modules.Analytics.Features.Dashboards;
using HyFib.HealthCare360.Modules.Analytics.Features.Reports;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Services;

namespace HyFib.HealthCare360.Modules.Analytics;

/// <summary>
/// MIS reports and managed BI (MIS-01..05, BI-01..05): a permission-filtered catalogue of hospital reports over every
/// module's data, Excel/CSV exports, executive KPIs, saved dashboards and scheduled report e-mails. Reports run on an
/// optional read-only replica (<c>ConnectionStrings:Analytics</c>) and never write to other modules' tables.
/// </summary>
public sealed class AnalyticsModule : IModule
{
    public string Name => "analytics";

    public IEnumerable<PermissionDefinition> Permissions => AnalyticsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ReportingDatabase(configuration));
        services.AddSingleton<ReportCatalog>();
        services.AddScoped<ReportingDbContextFactory>();
        services.AddScoped<FacilityAccess>();
        services.AddScoped<ReportActors>();
        services.AddScoped<ReportRunner>();
        services.AddScoped<ReportPresenter>();
        services.AddScoped<ReportAuditor>();
        services.AddScoped<KpiService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<ReportDeliveryService>();

        // Sends due scheduled e-mails every minute; returns immediately on API-only nodes (PlatformOptions.RunsJobs).
        services.AddHostedService<ScheduledReportJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ReportEndpoints.Map(endpoints);
        DashboardEndpoints.Map(endpoints);
        ScheduleEndpoints.Map(endpoints);
    }
}
