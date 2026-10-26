using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Ambulance;

/// <summary>
/// /api/v1/ambulance — the fleet, the dispatch board, each step of a trip, and the trip log. A trip for a registered
/// patient is opened through the patient access guard.
/// </summary>
internal static class AmbulanceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ambulance").WithTags("Ambulance");

        group.MapGet("/vehicles", async (AmbulanceService ambulance, CancellationToken ct) => TypedResults.Ok(await ambulance.VehiclesAsync(ct)))
            .RequireAnyPermission(SupportServicesPermissions.AmbulanceDispatch, SupportServicesPermissions.AmbulanceFleet);
        group.MapPost("/vehicles", async (SaveVehicleRequest request, AmbulanceService ambulance, CancellationToken ct) =>
                (await ambulance.SaveVehicleAsync(request, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.AmbulanceFleet).WithValidation<SaveVehicleRequest>();
        group.MapGet("/trips", async (AmbulanceService ambulance, CancellationToken ct) => TypedResults.Ok(await ambulance.ActiveAsync(ct)))
            .RequirePermission(SupportServicesPermissions.AmbulanceDispatch);
        group.MapGet("/trips/log", Log).RequirePermission(SupportServicesPermissions.AmbulanceDispatch);
        group.MapPost("/trips", Request).RequirePermission(SupportServicesPermissions.AmbulanceDispatch).WithValidation<RequestTripRequest>();
        group.MapPost("/trips/{id:guid}/dispatch", async (Guid id, DispatchRequest request, AmbulanceService ambulance, PatientAccessGuard guard,
                CancellationToken ct) => await OpenAsync(id, ambulance, guard, ct) ?? (await ambulance.DispatchAsync(id, request, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.AmbulanceDispatch).WithValidation<DispatchRequest>();
        group.MapPost("/trips/{id:guid}/at-scene", async (Guid id, TripStepRequest request, AmbulanceService ambulance, PatientAccessGuard guard,
                CancellationToken ct) => await OpenAsync(id, ambulance, guard, ct) ?? (await ambulance.ReachedSceneAsync(id, request.Version, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.AmbulanceDispatch).WithValidation<TripStepRequest>();
        group.MapPost("/trips/{id:guid}/left-scene", async (Guid id, TripStepRequest request, AmbulanceService ambulance, PatientAccessGuard guard,
                CancellationToken ct) => await OpenAsync(id, ambulance, guard, ct) ?? (await ambulance.LeftSceneAsync(id, request.Version, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.AmbulanceDispatch).WithValidation<TripStepRequest>();
        group.MapPost("/trips/{id:guid}/complete", async (Guid id, CompleteTripRequest request, AmbulanceService ambulance, PatientAccessGuard guard,
                CancellationToken ct) => await OpenAsync(id, ambulance, guard, ct) ?? (await ambulance.CompleteAsync(id, request, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.AmbulanceDispatch).WithValidation<CompleteTripRequest>();
        group.MapPost("/trips/{id:guid}/cancel", async (Guid id, CancelTripRequest request, AmbulanceService ambulance, PatientAccessGuard guard,
                CancellationToken ct) => await OpenAsync(id, ambulance, guard, ct) ?? (await ambulance.CancelAsync(id, request, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.AmbulanceDispatch).WithValidation<CancelTripRequest>();
    }

    private static async Task<IResult> Request(RequestTripRequest request, AmbulanceService ambulance, PatientAccessGuard guard, CancellationToken ct)
    {
        if (request.PatientId is { } patientId && await guard.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } refused)
        {
            return refused.Error!.ToProblem();
        }

        return (await ambulance.RequestAsync(request, ct)).ToOk();
    }

    private static async Task<IResult> Log(DateOnly? from, DateOnly? to, AmbulanceService ambulance, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-6);
        if (end < start || end.DayNumber - start.DayNumber > 92)
        {
            return Error.Field("to", "Choose up to 92 days, ending on or after the start.").ToProblem();
        }

        return TypedResults.Ok(await ambulance.LogAsync(start, end, ct));
    }

    /// <summary>Null when the trip may be worked on; otherwise the refusal.</summary>
    private static async Task<IResult?> OpenAsync(Guid id, AmbulanceService ambulance, PatientAccessGuard guard, CancellationToken ct)
    {
        if (await ambulance.FindAsync(id, ct) is not { } trip)
        {
            return Error.NotFound("Trip", id).ToProblem();
        }

        return trip.PatientId is { } patientId && await guard.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } refused
            ? refused.Error!.ToProblem()
            : null;
    }
}
