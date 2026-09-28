using HyFib.HealthCare360.Modules.Analytics.Services;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Dashboards;

/// <summary>/api/v1/analytics/dashboards — saved BI dashboards (BI-01).</summary>
internal static class DashboardEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/analytics/dashboards").WithTags("Analytics");

        group.MapGet("/", List).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapPost("/", Create).RequirePermission(AnalyticsPermissions.DashboardsManage).WithValidation<SaveDashboardRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(AnalyticsPermissions.DashboardsManage).WithValidation<SaveDashboardRequest>();
        group.MapPost("/{id:guid}/copy", Copy).RequirePermission(AnalyticsPermissions.DashboardsManage);
        group.MapDelete("/{id:guid}", Delete).RequirePermission(AnalyticsPermissions.DashboardsManage);
    }

    private static async Task<IResult> List(ReportActors actors, DashboardService dashboards, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? TypedResults.Ok(await dashboards.ListAsync(actor, ct)) : SignInRequired();

    private static async Task<IResult> Get(Guid id, ReportActors actors, DashboardService dashboards, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await dashboards.GetAsync(actor, id, ct)).ToOk() : SignInRequired();

    private static async Task<IResult> Create(SaveDashboardRequest request, ReportActors actors, DashboardService dashboards, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor
            ? (await dashboards.CreateAsync(actor, request, ct)).ToCreated(d => $"/api/v1/analytics/dashboards/{d.Id}")
            : SignInRequired();

    private static async Task<IResult> Update(Guid id, SaveDashboardRequest request, ReportActors actors, DashboardService dashboards, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await dashboards.UpdateAsync(actor, id, request, ct)).ToOk() : SignInRequired();

    private static async Task<IResult> Copy(Guid id, ReportActors actors, DashboardService dashboards, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor
            ? (await dashboards.CopyAsync(actor, id, ct)).ToCreated(d => $"/api/v1/analytics/dashboards/{d.Id}")
            : SignInRequired();

    private static async Task<IResult> Delete(Guid id, uint? version, ReportActors actors, DashboardService dashboards, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await dashboards.DeleteAsync(actor, id, version, ct)).ToNoContent() : SignInRequired();

    private static IResult SignInRequired() => Error.Forbidden("auth.required", "Sign in as a staff user to use analytics.").ToProblem();
}
