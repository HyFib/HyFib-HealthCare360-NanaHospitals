using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>
/// Changing a patient's registered mobile number with a code sent to the new number (<see cref="PatientPhoneChangeService"/>):
/// send the code, see the change still waiting for it, and confirm it with the code the patient reads out. For staff who
/// edit patients' details, as the edit is (<c>PUT /api/v1/patients/{id}</c>, which refuses a new number).
/// </summary>
internal static class PatientPhoneChangeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var patients = app.MapGroup("/api/v1/patients").WithTags("Patients");

        // 204 when nothing waits for a code, so a dialog opened again carries on where it was.
        patients.MapGet("/{id:guid}/phone-change", async (Guid id, PatientPhoneChangeService changes, CancellationToken ct) =>
                (await changes.PendingAsync(id, ct)).ToHttpResult(pending => pending is null ? (IResult)TypedResults.NoContent() : TypedResults.Ok(pending)))
            .RequirePermission(PatientsPermissions.Register).RequirePermission(PatientsPermissions.ContactRead);

        // 202: the code is on its way to the new number; nothing changes until it is entered.
        patients.MapPost("/{id:guid}/phone-change", async (Guid id, RequestPhoneChangeRequest request, PatientPhoneChangeService changes, CancellationToken ct) =>
                (await changes.RequestAsync(id, request, ct)).ToHttpResult(sent => TypedResults.Accepted($"/api/v1/patients/{id}/phone-change", sent)))
            .RequirePermission(PatientsPermissions.Register).RequirePermission(PatientsPermissions.ContactRead)
            .WithValidation<RequestPhoneChangeRequest>();

        patients.MapPost("/{id:guid}/phone-change/{changeId:guid}/confirm",
                async (Guid id, Guid changeId, ConfirmPhoneChangeRequest request, PatientPhoneChangeService changes, CancellationToken ct) =>
                    (await changes.ConfirmAsync(id, changeId, request, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Register).RequirePermission(PatientsPermissions.ContactRead)
            .WithValidation<ConfirmPhoneChangeRequest>();
    }
}
