using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Identity;

/// <summary>/api/v1/patients/…/identity-checks — checking a patient's identity with their Aadhaar fingerprint (INT-BIO).</summary>
internal static class IdentityCheckEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/patients").WithTags("Patients");

        group.MapGet("/identity-checks/setup", async (IdentityCheckService checks, CancellationToken ct) => TypedResults.Ok(await checks.SetupAsync(ct)))
            .RequirePermission(PatientsPermissions.IdentityVerify);

        group.MapGet("/{patientId:guid}/identity-checks", async (Guid patientId, IdentityCheckService checks, CancellationToken ct) =>
                (await checks.ListAsync(patientId, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Read);

        group.MapPost("/{patientId:guid}/identity-checks", async (Guid patientId, VerifyIdentityRequest request, IdentityCheckService checks, CancellationToken ct) =>
                (await checks.VerifyAsync(patientId, request, ct)).ToOk())
            .RequirePermission(PatientsPermissions.IdentityVerify)
            .WithValidation<VerifyIdentityRequest>();
    }
}
