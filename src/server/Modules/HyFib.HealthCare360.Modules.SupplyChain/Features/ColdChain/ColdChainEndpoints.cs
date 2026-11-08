using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.ColdChain;

/// <summary>/api/v1/inventory/cold-chain — refrigerators and freezers, their temperature log, excursions and the daily register.</summary>
internal static class ColdChainEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory/cold-chain").WithTags("Inventory");

        group.MapGet("/units", async (Guid? storeId, ColdChainService service, CancellationToken ct) => TypedResults.Ok(await service.UnitsAsync(storeId, ct)))
            .RequireAnyPermission(SupplyChainPermissions.ColdChainReaders);
        group.MapPost("/units", async (SaveColdStorageUnitRequest r, ColdChainService service, CancellationToken ct) =>
                (await service.SaveUnitAsync(null, r, ct)).ToCreated(id => $"/api/v1/inventory/cold-chain/units/{id}"))
            .RequirePermission(SupplyChainPermissions.ColdChainManage).WithValidation<SaveColdStorageUnitRequest>();
        group.MapPut("/units/{id:guid}", async (Guid id, SaveColdStorageUnitRequest r, ColdChainService service, CancellationToken ct) =>
                (await service.SaveUnitAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.ColdChainManage).WithValidation<SaveColdStorageUnitRequest>().RequireVersion();
        group.MapPost("/units/{id:guid}/logger-key", async (Guid id, ColdChainService service, CancellationToken ct) =>
                (await service.IssueLoggerKeyAsync(id, ct)).ToOk())
            .RequirePermission(SupplyChainPermissions.ColdChainManage);
        group.MapPost("/units/{id:guid}/logger-key/revoke", async (Guid id, ColdChainService service, CancellationToken ct) =>
                (await service.RevokeLoggerKeyAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.ColdChainManage);
        group.MapPost("/units/{id:guid}/readings", async (Guid id, RecordTemperatureRequest r, ColdChainService service, CancellationToken ct) =>
                (await service.RecordManualAsync(id, r, ct)).ToCreated(x => $"/api/v1/inventory/cold-chain/units/{id}/register"))
            .RequirePermission(SupplyChainPermissions.ColdChainRecord).WithValidation<RecordTemperatureRequest>();
        group.MapGet("/units/{id:guid}/register", async (Guid id, DateOnly? date, ColdChainService service, TimeProvider time, CancellationToken ct) =>
                (await service.RegisterAsync(id, date ?? HospitalClock.Today(time), ct)).ToOk())
            .RequireAnyPermission(SupplyChainPermissions.ColdChainReaders);
        group.MapGet("/excursions", async (Guid? storeId, bool? openOnly, ColdChainService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ExcursionsAsync(storeId, openOnly == true, ct)))
            .RequireAnyPermission(SupplyChainPermissions.ColdChainReaders);
        group.MapPost("/excursions/{id:guid}/action", async (Guid id, ExcursionActionRequest r, ColdChainService service, CancellationToken ct) =>
                (await service.RecordActionAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.ColdChainRecord).WithValidation<ExcursionActionRequest>();

        // A unit's data logger, with its own key: anonymous at the HTTP level and rate-limited, as the patient monitors are.
        app.MapPost(ColdChainService.ColdChainLoggerPath, Upload)
            .WithTags("Inventory")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Device)
            .WithValidation<LoggerUploadRequest>()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(256 * 1024));
    }

    /// <summary>
    /// Readings from a unit's data logger. The logger proves itself with its <c>X-Device-Key</c>, checked against the stored
    /// hash in constant time; an unknown or wrong key gets the same 401, so the endpoint cannot be used to find units.
    /// </summary>
    private static async Task<IResult> Upload(
        LoggerUploadRequest request,
        HttpRequest http,
        HospitalDbContext db,
        ColdChainService service,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var key = http.Headers[ColdChainService.ColdChainLoggerHeader].ToString();
        if (!ColdChainLoggerKeys.TryGetUnitId(key, out var unitId))
        {
            return NotRecognised();
        }

        // Anonymous request: no facility scope applies, the unit decides the facility.
        var unit = await db.Set<ColdStorageUnit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(u => u.Id == unitId, ct);
        if (unit is null || !unit.AcceptsLogger(key))
        {
            loggers.CreateLogger(typeof(ColdChainEndpoints)).LogWarning("Rejected cold-chain logger upload with an invalid key");
            return NotRecognised();
        }

        var result = await service.RecordFromLoggerAsync(unit, request.Readings, ct);
        return result.IsFailure ? result.Error!.ToProblem() : TypedResults.Ok(result.Value);
    }

    private static IResult NotRecognised() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The data logger key was not recognised.");
}
