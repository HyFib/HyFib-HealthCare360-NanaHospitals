using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emr;

/// <param name="CoveringReason">
/// A doctor acknowledging a critical lab result they did not order, where the lab asks for a second pathologist: that they
/// cover the patient, and why (on call, the unit's doctor on duty).
/// </param>
public sealed record AcknowledgeResultRequest(string? Note, string? CoveringReason = null);

public sealed class AcknowledgeResultRequestValidator : AbstractValidator<AcknowledgeResultRequest>
{
    public AcknowledgeResultRequestValidator()
    {
        RuleFor(x => x.Note).SafeText(500);
        RuleFor(x => x.CoveringReason).SafeText(500);
    }
}

/// <summary>/api/v1/emr — the physician desktop home screen (EMR-01) and acknowledgement of released results.</summary>
internal static class WorklistEndpoints
{
    /// <summary>
    /// The Diagnostics module's <c>LabResultService.SeparateVerifierSetting</c> (Clinical does not reference Diagnostics):
    /// where the lab asks for a second pathologist, a critical lab result is acknowledged only by a doctor who acts on it.
    /// </summary>
    internal const string LabSeparateVerifierSetting = "lab.separateVerifier";

    /// <summary>The kinds of practitioner who are doctors (a nurse's or technician's record is not).</summary>
    internal static readonly PractitionerKind[] DoctorKinds = [PractitionerKind.Consultant, PractitionerKind.Resident, PractitionerKind.Visiting];

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

    /// <summary>
    /// Records that the doctor has seen a released report (critical results stay on top until acknowledged). Where the lab
    /// asks for a second pathologist, a critical lab result is acknowledged only by its ordering doctor, or by a doctor who
    /// records that they cover the patient and why (audited).
    /// </summary>
    private static async Task<IResult> Acknowledge(Guid id, AcknowledgeResultRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        ISettingsService settings, IAuditLogger audit, TimeProvider time, CancellationToken ct)
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

        string? covering = null;
        if (review.HasCriticalFindings && review.Kind == DiagnosticKind.Laboratory
            && await settings.GetAsync<bool?>(LabSeparateVerifierSetting, review.FacilityId, ct) == true)
        {
            var doctor = user.PractitionerId is { } practitionerId
                         && await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId && DoctorKinds.Contains(p.Kind), ct)
                ? user.PractitionerId
                : null;
            var allowed = review.CanAcknowledgeCritical(doctor, request.CoveringReason);
            if (allowed.IsFailure)
            {
                return allowed.Error!.ToProblem();
            }

            if (doctor != review.PractitionerId)
            {
                covering = request.CoveringReason!.Trim();
                audit.Add(new AuditEntry(AuditCategories.Clinical, "result.critical.covering_acknowledged",
                    $"Critical result acknowledged by a covering doctor, not the ordering doctor: {review.Title} ({covering})")
                {
                    PatientId = review.PatientId,
                    EntityType = nameof(ResultReview),
                    EntityId = review.Id.ToString(),
                    Severity = AuditSeverity.Warning,
                    Details = new { orderingPractitionerId = review.PractitionerId, coveringPractitionerId = doctor, reason = covering },
                });
            }
        }

        var result = review.Acknowledge(user.UserId, request.Note, time.GetUtcNow(), covering);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
