using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>The override asked for: the analyte, the rejected runs locking it when it was asked, and why.</summary>
public sealed record QcOverridePayload(Guid LabTestId, IReadOnlyList<Guid> RejectedRunIds, string Reason);

/// <summary>
/// An override of today's failed QC for the rest of the day, where the lab asks for a second pathologist
/// (<see cref="LabResultService.SeparateVerifierSetting"/>). One pathologist overriding a failed QC run and then verifying
/// the results it locked would release unchecked results on one person's word: another holder of
/// <c>lab.results.verify</c>, never the one who asked, approves it, over the rejected runs as they were asked.
/// </summary>
internal sealed class QcOverrideApproval(QualityControlService qc) : IApprovalAction
{
    public const string Key = "lab.qc_override";

    public string Kind => Key;

    public string Title => "Override of failed lab QC";

    public string ApprovePermission => DiagnosticsPermissions.LabResultsVerify;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => qc.OverrideApprovedAsync(request, ct);
}
