using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Linen;

/// <summary>
/// /api/v1/linen — the linen list, the count at each place, ward par levels, and movements. A ward sends its soiled
/// linen and reports losses; the linen store and the laundry issue, receive, wash and condemn.
/// </summary>
internal static class LinenEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/linen").WithTags("Linen");

        group.MapGet("/items", async (LinenService linen, CancellationToken ct) => TypedResults.Ok(await linen.ItemsAsync(ct)))
            .RequireAnyPermission(SupportServicesPermissions.LinenWard, SupportServicesPermissions.LinenManage);
        group.MapPost("/items", async (SaveLinenItemRequest request, LinenService linen, CancellationToken ct) => (await linen.SaveItemAsync(request, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.LinenManage).WithValidation<SaveLinenItemRequest>();
        group.MapGet("/wards", async (LinenService linen, CancellationToken ct) => TypedResults.Ok(await linen.WardsAsync(ct)))
            .RequireAnyPermission(SupportServicesPermissions.LinenWard, SupportServicesPermissions.LinenManage);
        group.MapGet("/stock", async (LinenService linen, CancellationToken ct) => TypedResults.Ok(await linen.StockAsync(ct)))
            .RequireAnyPermission(SupportServicesPermissions.LinenWard, SupportServicesPermissions.LinenManage);
        group.MapPost("/par", async (SetParRequest request, LinenService linen, CancellationToken ct) => (await linen.SetParAsync(request, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.LinenManage).WithValidation<SetParRequest>();
        group.MapGet("/movements", Movements).RequireAnyPermission(SupportServicesPermissions.LinenWard, SupportServicesPermissions.LinenManage);
        group.MapPost("/movements", Record).RequireAnyPermission(SupportServicesPermissions.LinenWard, SupportServicesPermissions.LinenManage)
            .WithValidation<RecordLinenRequest>();
    }

    private static async Task<IResult> Record(RecordLinenRequest request, LinenService linen, IPermissionService permissions, ICurrentUser user, CancellationToken ct)
    {
        // A ward sends its soiled linen and reports what it has lost; the store and the laundry do the rest.
        if (request.Kind is not (LinenMovementKind.SoiledReturn or LinenMovementKind.Lost)
            && !await permissions.HasPermissionAsync(user.UserId!.Value, SupportServicesPermissions.LinenManage, ct))
        {
            return Error.Forbidden("linen.store_only", "Only the linen store and the laundry issue, receive, wash and condemn linen.").ToProblem();
        }

        return (await linen.RecordAsync(request, ct)).ToOk();
    }

    private static async Task<IResult> Movements(DateOnly? from, DateOnly? to, LinenMovementKind? kind, LinenService linen, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-6);
        if (end < start || end.DayNumber - start.DayNumber > 92)
        {
            return Error.Field("to", "Choose up to 92 days, ending on or after the start.").ToProblem();
        }

        return TypedResults.Ok(await linen.MovementsAsync(start, end, kind, ct));
    }
}
