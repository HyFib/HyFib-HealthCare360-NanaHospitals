using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Clinical;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.AI.Features.Clinical;

/// <summary>/api/v1/ai/clinical — dictation → SOAP draft for clinician review (never saved here).</summary>
internal static class ClinicalNoteEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ai/clinical/structure-note", StructureNote)
            .WithTags("AI assistant")
            .RequirePermission(AIPermissions.DictationUse)
            .RequireRateLimiting(AiRateLimits.Policy)
            .WithValidation<StructureNoteRequest>();
    }

    private static async Task<IResult> StructureNote(
        StructureNoteRequest request, IAiGateway ai, IAiQuota quota, PatientAccessGuard guard, ClinicalNoteStructurer structurer, CancellationToken ct)
    {
        var availability = await ai.GetAvailabilityAsync(ct);
        if (!availability.Enabled)
        {
            return AiProblems.Disabled(availability.Reason);
        }

        if (request.PatientId is { } patientId)
        {
            // Restricted (VIP) records need the same access here as anywhere else in the EMR.
            var access = await guard.EnsureCanOpenAsync(patientId, ct);
            if (access.IsFailure)
            {
                return access.Error!.ToProblem();
            }
        }

        if (await quota.CheckAsync(ct) is { } limited)
        {
            return AiProblems.QuotaExceeded(limited);
        }

        var outcome = await structurer.StructureAsync(request, ct);
        return outcome.Draft is { } draft ? TypedResults.Ok(draft) : AiProblems.ProviderFailed(outcome.Failure);
    }
}
