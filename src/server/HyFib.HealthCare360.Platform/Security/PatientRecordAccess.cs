using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// The restricted-record rules (SRS EHR-07) for code below the Patients module, such as the file store: whether the
/// signed-in user may open a patient's record now. The Patients module implements it with its access guard.
/// </summary>
public interface IPatientRecordAccess
{
    /// <summary>
    /// Success, or why not: 403 <c>patient.restricted</c> for a restricted (VIP) record until break-the-glass, unless the
    /// user may open restricted records; 404 for a patient who does not exist.
    /// </summary>
    Task<Result> EnsureCanOpenAsync(Guid patientId, CancellationToken cancellationToken);
}

/// <summary>
/// Used when the Patients module is not loaded (module tests): no patient's record can be opened, so a patient's data
/// is never shown because the rules were missing.
/// </summary>
internal sealed class NoPatientRecordAccess : IPatientRecordAccess
{
    public Task<Result> EnsureCanOpenAsync(Guid patientId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(Error.Forbidden("patient.restricted", "Patient records are not available here.")));
}
