using HyFib.HealthCare360.Modules.Inpatient.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.EarlyWarning;

/// <summary>/api/v1/nursing — NEWS2 early-warning escalations: the ward's open list, a stay's history, the response.</summary>
internal static class EarlyWarningEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/nursing").WithTags("Nursing");

        group.MapGet("/escalations", async (Guid? wardId, bool? openOnly, EarlyWarningService service, IAuditLogger audit, CancellationToken ct) =>
            {
                await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "ward.escalations.viewed", "Viewed the ward's early-warning escalations")
                {
                    EntityType = "Ward",
                    EntityId = wardId?.ToString(),
                }, ct);
                return TypedResults.Ok(await service.ForWardAsync(wardId, openOnly ?? true, ct));
            })
            .RequireAnyPermission(InpatientPermissions.NursingChart, InpatientPermissions.NursingInCharge);

        group.MapGet("/admissions/{admissionId:guid}/escalations", async (Guid admissionId, AdmissionAccess admissions, EarlyWarningService service, CancellationToken ct) =>
            {
                var access = await admissions.OpenForReadAsync(admissionId, "Viewed early-warning escalations", ct);
                return access.IsFailure ? access.Error!.ToProblem() : TypedResults.Ok(await service.ForAdmissionAsync(admissionId, ct));
            })
            .RequireAnyPermission(InpatientPermissions.NursingChart, InpatientPermissions.NursingInCharge);

        group.MapPost("/escalations/{id:guid}/respond", async (Guid id, RespondToEscalationRequest request, EarlyWarningService service, CancellationToken ct) =>
                (await service.RespondAsync(id, request, ct)).ToOk())
            .RequireAnyPermission(InpatientPermissions.NursingChart, InpatientPermissions.NursingInCharge)
            .WithValidation<RespondToEscalationRequest>();
    }
}
