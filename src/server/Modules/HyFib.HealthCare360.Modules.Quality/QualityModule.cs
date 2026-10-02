using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Modules.Quality.Features.Dashboard;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Modules.Quality.Features.Indicators;
using HyFib.HealthCare360.Modules.Quality.Features.Risks;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality;

public static class QualityPermissions
{
    /// <summary>Every staff member may report incidents (and progress CAPAs they own).</summary>
    public const string IncidentsReport = "quality.incidents.report";

    public const string IncidentsManage = "quality.incidents.manage";
    public const string CapaManage = "quality.capa.manage";
    public const string RisksManage = "quality.risks.manage";
    public const string IndicatorsManage = "quality.indicators.manage";

    private static readonly string[] Quality = [SystemRoles.QualityManager, SystemRoles.HospitalAdmin];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(IncidentsReport, "Quality", "Report incidents and near misses (optionally anonymously); work on own corrective actions", PlatformPermissions.AllStaff),
        new(IncidentsManage, "Quality", "Review, grade and investigate incidents; receive sentinel-event alerts", Quality),
        new(CapaManage, "Quality", "Plan corrective/preventive actions and verify their effectiveness", Quality),
        new(RisksManage, "Quality", "Maintain the risk register", Quality),
        new(IndicatorsManage, "Quality", "Define quality indicators and record their values", Quality),
    ];
}

/// <summary>IRMS: incident reporting, root-cause analysis, CAPA, risk register and NABH quality indicators (SRS IRMS-01..06).</summary>
public sealed class QualityModule : IModule
{
    public string Name => "quality";

    public IEnumerable<PermissionDefinition> Permissions => QualityPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => QualityNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IncidentService>();
        services.AddScoped<CapaService>();
        services.AddScoped<AnonymousReportingScope>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        IncidentEndpoints.Map(endpoints);
        InvestigationEndpoints.Map(endpoints);
        CapaEndpoints.Map(endpoints);
        RiskEndpoints.Map(endpoints);
        IndicatorEndpoints.Map(endpoints);
        QualityDashboardEndpoints.Map(endpoints);
    }
}
