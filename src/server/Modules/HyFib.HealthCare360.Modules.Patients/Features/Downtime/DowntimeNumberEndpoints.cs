using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Downtime;

/// <summary>
/// /api/v1/downtime/number-blocks — visit, bill and receipt numbers issued ahead to a facility's counters (NABH IMS);
/// /api/v1/downtime/back-entry — the back-entry windows and late back-entry approvals.
/// </summary>
internal static class DowntimeNumberEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var blocks = app.MapGroup("/api/v1/downtime/number-blocks").WithTags("Downtime").RequirePermission(PatientsPermissions.Downtime);

        blocks.MapGet("/", async (DowntimeNumberKind? kind, DowntimeNumberService service, CancellationToken ct) => (await service.ListAsync(kind, ct)).ToOk());

        blocks.MapGet("/{id:guid}", async (Guid id, DowntimeNumberService service, CancellationToken ct) => (await service.GetAsync(id, ct)).ToOk());

        blocks.MapPost("/", async (IssueDowntimeNumbersRequest request, DowntimeNumberService service, CancellationToken ct) =>
                (await service.IssueAsync(request, ct)).ToCreated(d => $"/api/v1/downtime/number-blocks/{d.Block.Id}"))
            .WithValidation<IssueDowntimeNumbersRequest>();

        blocks.MapPost("/{id:guid}/void", async (Guid id, VoidDowntimeNumberRequest request, DowntimeNumberService service, CancellationToken ct) =>
                (await service.VoidAsync(id, request, ct)).ToNoContent())
            .WithValidation<VoidDowntimeNumberRequest>();

        blocks.MapPost("/{id:guid}/return", async (Guid id, ReturnDowntimeBlockRequest request, DowntimeNumberService service, CancellationToken ct) =>
                (await service.ReturnAsync(id, request, ct)).ToOk())
            .WithValidation<ReturnDowntimeBlockRequest>();

        var backEntry = app.MapGroup("/api/v1/downtime/back-entry").WithTags("Downtime");

        backEntry.MapGet("/windows", async (BackEntryService service, CancellationToken ct) => TypedResults.Ok(await service.WindowsAsync(ct)))
            .RequirePermission(PatientsPermissions.BackEntry);

        backEntry.MapGet("/late", async (BackEntryService service, CancellationToken ct) => TypedResults.Ok(await service.GrantsAsync(ct)))
            .RequirePermission(PatientsPermissions.BackEntry);

        backEntry.MapPost("/late", async (RequestLateBackEntry request, BackEntryService service, CancellationToken ct) =>
                (await service.RequestLateAsync(request, ct)).ToHttpResult(asked => TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked)))
            .RequirePermission(PatientsPermissions.BackEntry)
            .WithValidation<RequestLateBackEntry>();

        // The pre-downtime pack: printed now (kept, audited), or the latest kept (the nightly one or a later print).
        var pack = app.MapGroup("/api/v1/downtime/pack").WithTags("Downtime").RequirePermission(PatientsPermissions.DowntimePack);

        pack.MapPost("/", async (DowntimePackService service, CancellationToken ct) => (await service.PrintAsync(ct)).ToOk());

        pack.MapGet("/latest", async (DowntimePackService service, CancellationToken ct) => (await service.LatestAsync(ct)).ToOk());
    }
}
