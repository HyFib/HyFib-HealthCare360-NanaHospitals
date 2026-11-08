using HyFib.HealthCare360.Modules.Analytics.Services;

namespace HyFib.HealthCare360.Modules.Analytics.Features.KpiTargets;

/// <summary>/api/v1/analytics/kpi-targets — each KPI's target and amber and red thresholds for the signed-in facility.</summary>
internal static class KpiTargetEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/analytics/kpi-targets").WithTags("Analytics").RequirePermission(AnalyticsPermissions.KpiTargetsManage);

        group.MapGet("/", List);
        group.MapPut("/{key}", Save).WithValidation<SaveKpiTargetRequest>().FirstSaveCreates();
        group.MapDelete("/{key}", Delete);
    }

    private static async Task<IResult> List(ReportActors actors, KpiTargetService targets, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await targets.ListAsync(actor, ct)).ToOk() : SignInRequired();

    private static async Task<IResult> Save(string key, SaveKpiTargetRequest request, ReportActors actors, KpiTargetService targets, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await targets.SaveAsync(actor, key, request, ct)).ToOk() : SignInRequired();

    private static async Task<IResult> Delete(string key, uint? version, ReportActors actors, KpiTargetService targets, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await targets.DeleteAsync(actor, key, version, ct)).ToNoContent() : SignInRequired();

    private static IResult SignInRequired() => Error.Forbidden("auth.required", "Sign in as a staff user to use analytics.").ToProblem();
}
