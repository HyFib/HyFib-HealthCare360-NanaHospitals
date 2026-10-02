using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Inpatient.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;

/// <summary>/api/v1/inpatient/rounds — the ward-round section of the doctor's worklist (EMR-01).</summary>
internal static class RoundEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        // Admissions and clinical flags together: the caller needs both the bed board and the clinical record.
        app.MapGet("/api/v1/inpatient/rounds", Rounds)
            .WithTags("Admissions")
            .RequirePermission(InpatientPermissions.AdmissionsRead)
            .RequirePermission(ClinicalPermissions.EmrRead);

    /// <summary>Defaults to the signed-in doctor; without a doctor, the department's in-patients.</summary>
    private static async Task<IResult> Rounds(Guid? practitionerId, Guid? departmentId, WardRoundQueries queries, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok(await queries.BuildAsync(practitionerId ?? user.PractitionerId, departmentId, ct));
}
