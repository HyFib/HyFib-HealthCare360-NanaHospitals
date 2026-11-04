using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>The certificate to confirm. The approval's version is the certificate's when confirmation was asked for.</summary>
public sealed record DeathConfirmationPayload(Guid DeathId);

/// <summary>
/// A second doctor's confirmation of a death certificate (MCCD, Form 4) before the hospital reports the death to the
/// Registrar: the doctor who certifies it asks, and another doctor, never the same one, confirms the certificate as it
/// then stands by approving the request. The death itself is recorded at once (the patient is deceased, the mortuary and
/// bookings follow); only the report waits. A facility with one doctor switches it off
/// (<see cref="VitalRegistrationService.ConfirmationSetting"/>).
/// </summary>
internal sealed class DeathCertificateApproval(VitalRegistrationService vital) : IApprovalAction
{
    public const string Key = "mrd.death_certificate";

    public string Kind => Key;

    public string Title => "Death certificate";

    public string ApprovePermission => ClinicalPermissions.DeathsConfirm;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => vital.ConfirmApprovedAsync(request, ct);
}
