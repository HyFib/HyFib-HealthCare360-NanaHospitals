using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>
/// "Confirm your details" in the patient portal (<see cref="PortalRecordConfirmationService"/>): how many records on the
/// account's number wait to be confirmed, and confirming them with the patient's date of birth or UHID.
/// </summary>
internal static class PortalRecordConfirmationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/api/v1/portal/records-to-confirm").WithTags("Patient Portal");
        portal.MapGet("/", async (PortalRecordConfirmationService confirmations, CancellationToken ct) => (await confirmations.StatusAsync(ct)).ToOk())
            .RequirePermission(PatientsPermissions.Portal).RequireFeature(FeatureKeys.PatientPortal);
        portal.MapPost("/", async (ConfirmRecordsRequest request, PortalRecordConfirmationService confirmations, CancellationToken ct) =>
                (await confirmations.ConfirmAsync(request, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Portal).RequireFeature(FeatureKeys.PatientPortal).WithValidation<ConfirmRecordsRequest>();
    }
}
