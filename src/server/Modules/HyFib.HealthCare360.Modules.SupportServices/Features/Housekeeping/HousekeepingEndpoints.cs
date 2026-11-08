using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Housekeeping;

/// <summary>
/// /api/v1/housekeeping: areas cleaned on a schedule with their checklists, the day's board, cleaning asked for by the
/// wards (spill, terminal, routine) with response times, verification by a supervisor other than the cleaner, and the
/// period's report.
/// </summary>
internal static class HousekeepingEndpoints
{
    private static readonly string[] Readers =
    [
        SupportServicesPermissions.HousekeepingWork, SupportServicesPermissions.HousekeepingVerify, SupportServicesPermissions.HousekeepingManage,
    ];

    private static readonly string[] RequestReaders = [.. Readers, SupportServicesPermissions.HousekeepingRequest];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/housekeeping").WithTags("Housekeeping");

        group.MapGet("/areas", async (bool? includeInactive, HousekeepingService s, CancellationToken ct) => TypedResults.Ok(await s.AreasAsync(includeInactive == true, ct)))
            .RequireAnyPermission(Readers);
        group.MapPost("/areas", async (SaveCleaningAreaRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.SaveAreaAsync(null, r, ct)).ToHttpResult(a => TypedResults.Created($"/api/v1/housekeeping/areas/{a.Id}", a)))
            .RequirePermission(SupportServicesPermissions.HousekeepingManage).WithValidation<SaveCleaningAreaRequest>();
        group.MapPut("/areas/{id:guid}", async (Guid id, SaveCleaningAreaRequest r, HousekeepingService s, CancellationToken ct) => (await s.SaveAreaAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.HousekeepingManage).WithValidation<SaveCleaningAreaRequest>().RequireVersion();

        group.MapGet("/board", async (DateOnly? date, HousekeepingService s, TimeProvider time, CancellationToken ct) =>
                TypedResults.Ok(await s.BoardAsync(date ?? time.Today(), ct)))
            .RequireAnyPermission(Readers);
        group.MapPost("/areas/{id:guid}/cleanings", async (Guid id, RecordCleaningRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.RecordAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.HousekeepingWork).WithValidation<RecordCleaningRequest>();
        group.MapPost("/cleanings/{id:guid}/verify", async (Guid id, VerifyCleaningRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.VerifyRecordAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.HousekeepingVerify).WithValidation<VerifyCleaningRequest>();

        group.MapGet("/requests", async (bool? open, DateOnly? from, DateOnly? to, HousekeepingService s, TimeProvider time, CancellationToken ct) =>
            {
                var (f, t) = Range(from, to, time);
                return TypedResults.Ok(await s.RequestsAsync(open != false && from is null && to is null, f, t, ct));
            })
            .RequireAnyPermission(RequestReaders);
        group.MapPost("/requests", async (RaiseCleaningRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.RaiseAsync(r, ct)).ToHttpResult(x => TypedResults.Created($"/api/v1/housekeeping/requests/{x.Id}", x)))
            .RequireAnyPermission(SupportServicesPermissions.HousekeepingRequest, SupportServicesPermissions.HousekeepingManage).WithValidation<RaiseCleaningRequest>();
        group.MapPost("/requests/{id:guid}/acknowledge", async (Guid id, CleaningRequestStepRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.AcknowledgeAsync(id, r.Version, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.HousekeepingWork).WithValidation<CleaningRequestStepRequest>();
        group.MapPost("/requests/{id:guid}/complete", async (Guid id, CompleteCleaningRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.CompleteAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.HousekeepingWork).WithValidation<CompleteCleaningRequest>();
        group.MapPost("/requests/{id:guid}/verify", async (Guid id, VerifyCleaningRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.VerifyRequestAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.HousekeepingVerify).WithValidation<VerifyCleaningRequest>();
        group.MapPost("/requests/{id:guid}/cancel", async (Guid id, CancelCleaningRequest r, HousekeepingService s, CancellationToken ct) =>
                (await s.CancelAsync(id, r, ct)).ToOk())
            .RequireAnyPermission(SupportServicesPermissions.HousekeepingRequest, SupportServicesPermissions.HousekeepingManage).WithValidation<CancelCleaningRequest>();

        group.MapGet("/report", async (DateOnly? from, DateOnly? to, HousekeepingService s, TimeProvider time, CancellationToken ct) =>
            {
                var (f, t) = Range(from, to, time);
                return TypedResults.Ok(await s.ReportAsync(f, t, ct));
            })
            .RequireAnyPermission(SupportServicesPermissions.HousekeepingVerify, SupportServicesPermissions.HousekeepingManage);
    }

    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, TimeProvider time)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-6);
        return start > end ? (end, end) : (start, end.DayNumber - start.DayNumber > 92 ? start.AddDays(92) : end);
    }
}
