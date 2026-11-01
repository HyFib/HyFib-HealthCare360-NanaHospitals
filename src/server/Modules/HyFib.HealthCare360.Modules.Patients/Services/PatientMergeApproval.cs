using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>The merge asked for: the duplicate goes into the record kept, as that record was when it was asked for.</summary>
public sealed record PatientMergePayload(Guid DuplicateId, Guid IntoPatientId, uint IntoVersion, string Reason);

/// <summary>
/// Merging a duplicate record into another: one person who may merge records asks, another approves. It cannot be undone
/// and moves one person's whole record (visits, results, consents, documents, portal links) onto another's, so one
/// mistaken merge would show a stranger's chart under the patient's name.
/// </summary>
internal sealed class PatientMergeApproval(PatientRegistrationService registration) : IApprovalAction
{
    public const string Key = "patients.merge";

    public string Kind => Key;

    public string Title => "Patient merge";

    public string ApprovePermission => PatientsPermissions.Merge;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => registration.MergeApprovedAsync(request, ct);
}
