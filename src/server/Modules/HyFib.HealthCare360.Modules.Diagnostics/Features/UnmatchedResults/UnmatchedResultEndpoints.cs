using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.UnmatchedResults;

/// <summary>
/// /api/v1/lab/unmatched-results: analyser results that could not go on an open test (no or an unknown sample id, a
/// sample not in the lab, a value the test cannot hold, another value for a verified test), for the lab to assign to
/// the right sample's test or to discard with a reason (INT-LAB, LIS-04). Whoever enters results resolves them.
/// </summary>
internal static class UnmatchedResultEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var unmatched = app.MapGroup("/api/v1/lab/unmatched-results").WithTags("Laboratory");
        unmatched.MapGet("/", List).RequireAnyPermission(DiagnosticsPermissions.LabResultsEnter, DiagnosticsPermissions.LabResultsVerify);
        unmatched.MapPost("/{id:guid}/assign", Assign)
            .RequirePermission(DiagnosticsPermissions.LabResultsEnter)
            .WithValidation<AssignUnmatchedResultRequest>();
        unmatched.MapPost("/{id:guid}/discard", Discard)
            .RequirePermission(DiagnosticsPermissions.LabResultsEnter)
            .WithValidation<DiscardUnmatchedResultRequest>();
    }

    /// <summary>Waiting results by default (the lab's worklist and its count); assigned or discarded ones on request.</summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query, UnmatchedResultStatus? status, UnmatchedResultService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListAsync(query.ToRequest(), status ?? UnmatchedResultStatus.Open, ct));

    private static async Task<IResult> Assign(Guid id, AssignUnmatchedResultRequest request, UnmatchedResultService service, CancellationToken ct) =>
        (await service.AssignAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> Discard(Guid id, DiscardUnmatchedResultRequest request, UnmatchedResultService service, CancellationToken ct) =>
        (await service.DiscardAsync(id, request.Reason, ct)).ToNoContent();
}
