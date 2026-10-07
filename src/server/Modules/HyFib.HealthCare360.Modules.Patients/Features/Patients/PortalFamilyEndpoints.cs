using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>
/// Family members on other mobile numbers in the patient portal (PP-01): staff share a patient's records with a family
/// member's account at the front desk, and in the portal an account holder adds a family member with a code sent to
/// the family member's own number. Both sides can end a link.
/// </summary>
internal static class PortalFamilyEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var patients = app.MapGroup("/api/v1/patients").WithTags("Patients");
        patients.MapGet("/{patientId:guid}/portal-family", async (Guid patientId, PortalFamilyService family, CancellationToken ct) =>
                (await family.AccessAsync(patientId, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Read);
        patients.MapPost("/{patientId:guid}/portal-family", async (Guid patientId, LinkFamilyMemberRequest r, PortalFamilyService family, CancellationToken ct) =>
                (await family.LinkAtFrontDeskAsync(patientId, r, ct)).ToCreated(_ => $"/api/v1/patients/{patientId}/portal-family"))
            .RequirePermission(PatientsPermissions.Consents).WithValidation<LinkFamilyMemberRequest>();
        patients.MapPost("/portal-family/{linkId:guid}/revoke", async (Guid linkId, RevokeFamilyLinkRequest r, PortalFamilyService family, CancellationToken ct) =>
                (await family.RevokeAtFrontDeskAsync(linkId, r.Reason, ct)).ToNoContent())
            .RequirePermission(PatientsPermissions.Consents).WithValidation<RevokeFamilyLinkRequest>();

        var portal = app.MapGroup("/api/v1/portal/family").WithTags("Patient Portal");
        portal.MapGet("/", async (PortalFamilyService family, CancellationToken ct) => (await family.PortalFamilyAsync(ct)).ToOk())
            .RequirePermission(PatientsPermissions.Portal).RequireFeature(FeatureKeys.PatientPortal);
        portal.MapPost("/codes", async (RequestFamilyCodeRequest r, PortalFamilyService family, CancellationToken ct) =>
                (await family.RequestCodeAsync(r, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Portal).RequireFeature(FeatureKeys.PatientPortal).WithValidation<RequestFamilyCodeRequest>();
        portal.MapPost("/codes/{codeId:guid}/verify", async (Guid codeId, VerifyFamilyCodeRequest r, PortalFamilyService family, CancellationToken ct) =>
                (await family.VerifyCodeAsync(codeId, r.Code, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Portal).RequireFeature(FeatureKeys.PatientPortal).WithValidation<VerifyFamilyCodeRequest>();
        portal.MapPost("/links/{linkId:guid}/end", async (Guid linkId, PortalFamilyService family, CancellationToken ct) =>
                (await family.EndInPortalAsync(linkId, ct)).ToNoContent())
            .RequirePermission(PatientsPermissions.Portal).RequireFeature(FeatureKeys.PatientPortal);
    }
}
