using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Which patients a Patient Portal user may see: every active patient registered with the mobile number the user
/// verified by OTP (families in India commonly share one number). Portal endpoints in any module must call
/// <see cref="CanAccessAsync"/> (or filter with <see cref="AccessiblePatientIdsAsync"/>) before returning data.
/// </summary>
public sealed class PortalPatientScope(HospitalDbContext db, ICurrentUser currentUser)
{
    private IReadOnlyList<Guid>? _cached;

    public async Task<IReadOnlyList<Guid>> AccessiblePatientIdsAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        if (!currentUser.IsPatient || currentUser.UserId is not { } userId)
        {
            return _cached = [];
        }

        var phone = await db.Set<User>().Where(u => u.Id == userId && u.IsActive).Select(u => u.Phone).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(phone))
        {
            return _cached = [];
        }

        _cached = await db.Set<Patient>()
            .Where(p => p.Phone == phone && p.IsActive && p.MergedIntoPatientId == null)
            .OrderBy(p => p.RegisteredAt)
            .Select(p => p.Id)
            .ToListAsync(ct);
        return _cached;
    }

    public async Task<bool> CanAccessAsync(Guid patientId, CancellationToken ct) =>
        (await AccessiblePatientIdsAsync(ct)).Contains(patientId);

    /// <summary>Returns a Forbidden error unless the portal user may see <paramref name="patientId"/>.</summary>
    public async Task<Result> EnsureAccessAsync(Guid patientId, CancellationToken ct) =>
        await CanAccessAsync(patientId, ct)
            ? Result.Success()
            : Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.");
}
