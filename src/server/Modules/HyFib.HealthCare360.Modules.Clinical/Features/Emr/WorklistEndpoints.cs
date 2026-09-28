using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emr;

public sealed record AcknowledgeResultRequest(string? Note);

public sealed class AcknowledgeResultRequestValidator : AbstractValidator<AcknowledgeResultRequest>
{
    public AcknowledgeResultRequestValidator() => RuleFor(x => x.Note).SafeText(500);
}

/// <summary>/api/v1/emr — the physician desktop home screen (EMR-01) and acknowledgement of released results.</summary>
internal static class WorklistEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/worklist", Worklist).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/results/{id:guid}/acknowledge", Acknowledge).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<AcknowledgeResultRequest>();
    }

    /// <summary>Defaults: today (IST), the signed-in doctor and their department.</summary>
    private static async Task<IResult> Worklist(DateOnly? date, Guid? practitionerId, Guid? departmentId, WorklistQueries queries, ICurrentUser user,
        TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(await queries.BuildAsync(date ?? time.Today(), practitionerId ?? user.PractitionerId, departmentId, ct));

    /// <summary>Records that the doctor has seen a released report (critical results stay on top until acknowledged).</summary>
    private static async Task<IResult> Acknowledge(Guid id, AcknowledgeResultRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        TimeProvider time, CancellationToken ct)
    {
        var review = await db.Set<ResultReview>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (review is null)
        {
            return Error.NotFound("Result", id).ToProblem();
        }

        var opened = await access.OpenAsync(review.PatientId, $"Reviewed result: {review.Title}", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var result = review.Acknowledge(user.UserId, request.Note, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
