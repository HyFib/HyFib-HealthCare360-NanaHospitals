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

/// <summary>The confirmed certificate to mark entered in error, and the time of death whose deceased mark it took.</summary>
public sealed record DeathVoidPayload(Guid DeathId, DateTimeOffset DiedAt);

/// <summary>
/// A death certificate a second doctor confirmed, marked entered in error (maker-checker, slice 8): making it took two
/// doctors, so undoing it does too. Its certifier asks, with the reason, and another doctor who confirms certificates
/// (often the one who confirmed it) agrees; never the one who asked. Until then the certificate and the patient's deceased
/// status stand. A certificate reported to the Registrar is not voided here at all: a correction goes through the Registrar.
/// </summary>
internal sealed class DeathCertificateVoidApproval(VitalRegistrationService vital) : IApprovalAction
{
    public const string Key = "mrd.death_certificate_void";

    public string Kind => Key;

    public string Title => "Confirmed death certificate voided";

    public string ApprovePermission => ClinicalPermissions.DeathsConfirm;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => vital.ApplyVoidAsync(request, ct);
}
