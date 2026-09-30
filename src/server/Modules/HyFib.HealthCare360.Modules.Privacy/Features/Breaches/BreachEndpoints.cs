using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Breaches;

/// <summary>/api/v1/privacy/breaches: the personal-data breach register (DPDP Act s8(6); DPDP Rules 2025, rule 7).</summary>
internal static class BreachEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/privacy/breaches").WithTags("Privacy").RequirePermission(PrivacyPermissions.BreachesManage);

        group.MapGet("/", async ([AsParameters] PageQuery query, bool? openOnly, BreachService breaches, CancellationToken ct) =>
            TypedResults.Ok(await breaches.ListAsync(openOnly == true, query.ToRequest(), ct)));
        group.MapGet("/{id:guid}", async (Guid id, BreachService breaches, CancellationToken ct) =>
            await breaches.DetailAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : Error.NotFound(nameof(Breach), id).ToProblem());
        group.MapPost("/", async (SaveBreachRequest r, BreachService breaches, CancellationToken ct) =>
                (await breaches.CreateAsync(r, ct)).ToCreated(created => $"/api/v1/privacy/breaches/{created.Id}"))
            .WithValidation<SaveBreachRequest>();
        group.MapPut("/{id:guid}", async (Guid id, SaveBreachRequest r, BreachService breaches, CancellationToken ct) =>
                (await breaches.UpdateAsync(id, r, ct)).ToNoContent())
            .WithValidation<SaveBreachRequest>();
        group.MapPost("/{id:guid}/board-informed", async (Guid id, BoardInformedRequest r, BreachService breaches, CancellationToken ct) =>
                (await breaches.RecordBoardInformedAsync(id, r, ct)).ToNoContent())
            .WithValidation<BoardInformedRequest>();
        group.MapPost("/{id:guid}/extension", async (Guid id, BoardExtensionRequest r, BreachService breaches, CancellationToken ct) =>
                (await breaches.RecordExtensionAsync(id, r, ct)).ToNoContent())
            .WithValidation<BoardExtensionRequest>();
        group.MapPost("/{id:guid}/report-submitted", async (Guid id, ReportSubmittedRequest r, BreachService breaches, CancellationToken ct) =>
                (await breaches.RecordReportSubmittedAsync(id, r, ct)).ToNoContent())
            .WithValidation<ReportSubmittedRequest>();
        group.MapPost("/{id:guid}/close", async (Guid id, CloseBreachRequest r, BreachService breaches, CancellationToken ct) =>
                (await breaches.CloseAsync(id, r, ct)).ToNoContent())
            .WithValidation<CloseBreachRequest>();
        group.MapGet("/{id:guid}/report", async (Guid id, BreachService breaches, CancellationToken ct) =>
            await breaches.ReportAsync(id, ct) is { } report ? TypedResults.Ok(report) : Error.NotFound(nameof(Breach), id).ToProblem());

        group.MapGet("/{id:guid}/affected", async (Guid id, [AsParameters] PageQuery query, BreachService breaches, CancellationToken ct) =>
            TypedResults.Ok(await breaches.AffectedAsync(id, query.ToRequest(), ct)));
        group.MapPost("/{id:guid}/affected", async (Guid id, AddAffectedRequest r, BreachService breaches, CancellationToken ct) =>
                (await breaches.AddAffectedAsync(id, r, ct)).ToOk())
            .WithValidation<AddAffectedRequest>();
        group.MapDelete("/{id:guid}/affected/{patientId:guid}", async (Guid id, Guid patientId, BreachService breaches, CancellationToken ct) =>
            (await breaches.RemoveAffectedAsync(id, patientId, ct)).ToNoContent());
        group.MapPost("/{id:guid}/notify", async (Guid id, BreachService breaches, CancellationToken ct) =>
            (await breaches.NotifyAsync(id, ct)).ToOk());
    }
}
