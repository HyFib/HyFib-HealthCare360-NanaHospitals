using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>
/// Verifying a patient's registered mobile number with a code sent to it (<see cref="PatientPhoneVerificationService"/>):
/// send the code, see the verification still waiting for it, and confirm it with the code the patient reads out. For the
/// staff who edit patients' details, as a number change is (<see cref="PatientPhoneChangeEndpoints"/>).
/// </summary>
internal static class PatientPhoneVerificationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var patients = app.MapGroup("/api/v1/patients").WithTags("Patients");

        // 204 when nothing waits for a code, so a dialog opened again carries on where it was.
        patients.MapGet("/{id:guid}/phone-verification", async (Guid id, PatientPhoneVerificationService verifications, CancellationToken ct) =>
                (await verifications.PendingAsync(id, ct)).ToHttpResult(pending => pending is null ? (IResult)TypedResults.NoContent() : TypedResults.Ok(pending)))
            .RequirePermission(PatientsPermissions.Register).RequirePermission(PatientsPermissions.ContactRead);

        // 202: the code is on its way to the number; nothing is verified until it is entered.
        patients.MapPost("/{id:guid}/phone-verification", async (Guid id, PatientPhoneVerificationService verifications, CancellationToken ct) =>
                (await verifications.RequestAsync(id, ct)).ToHttpResult(sent => TypedResults.Accepted($"/api/v1/patients/{id}/phone-verification", sent)))
            .RequirePermission(PatientsPermissions.Register).RequirePermission(PatientsPermissions.ContactRead);

        patients.MapPost("/{id:guid}/phone-verification/{verificationId:guid}/confirm",
                async (Guid id, Guid verificationId, ConfirmPhoneVerificationRequest request, PatientPhoneVerificationService verifications, CancellationToken ct) =>
                    (await verifications.ConfirmAsync(id, verificationId, request, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Register).RequirePermission(PatientsPermissions.ContactRead)
            .WithValidation<ConfirmPhoneVerificationRequest>();
    }
}
