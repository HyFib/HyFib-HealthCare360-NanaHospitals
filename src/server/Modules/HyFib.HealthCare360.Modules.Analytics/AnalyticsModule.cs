namespace HyFib.HealthCare360.Modules.Analytics;

/// <summary>MIS reports and managed BI dashboards (MIS-01..05, BI-01..05).</summary>
public sealed class AnalyticsModule : IModule
{
    public string Name => "analytics";

    public IEnumerable<PermissionDefinition> Permissions => AnalyticsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
