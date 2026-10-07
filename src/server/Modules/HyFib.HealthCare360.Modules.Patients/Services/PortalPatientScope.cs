using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Which patients a Patient Portal user may see: every active patient registered with the mobile number the user
/// verified by OTP (families in India commonly share one number), and the family members on other numbers that a
/// <see cref="PortalFamilyLink"/> in force gives the account (PP-01). Portal endpoints in any module must call
/// <see cref="CanAccessAsync"/> (or filter with <see cref="AccessiblePatientIdsAsync"/>) before returning data.
/// </summary>
public sealed class PortalPatientScope(HospitalDbContext db, ICurrentUser currentUser)
{
    private IReadOnlyList<Guid>? _cached;
    private string? _phone;
    private bool _phoneRead;

    /// <summary>The mobile number (E.164) the signed-in portal user verified, or null for anyone else.</summary>
    public async Task<string?> AccountPhoneAsync(CancellationToken ct)
    {
        if (!_phoneRead)
        {
            _phone = currentUser.IsPatient && currentUser.UserId is { } userId
                ? await db.Set<User>().Where(u => u.Id == userId && u.IsActive).Select(u => u.Phone).FirstOrDefaultAsync(ct)
                : null;
            _phoneRead = true;
        }

        return string.IsNullOrEmpty(_phone) ? null : _phone;
    }

    /// <summary>The account's patients: those on its own number first, then linked family members, each oldest first.</summary>
    public async Task<IReadOnlyList<Guid>> AccessiblePatientIdsAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        if (await AccountPhoneAsync(ct) is not { } phone)
        {
            return _cached = [];
        }

        var linked = db.Set<PortalFamilyLink>().Where(l => l.AccountPhone == phone && l.RevokedAt == null).Select(l => l.PatientId);
        _cached = await db.Set<Patient>()
            .Where(p => (p.Phone == phone || linked.Contains(p.Id)) && p.IsActive && p.MergedIntoPatientId == null)
            .OrderBy(p => p.Phone == phone ? 0 : 1)
            .ThenBy(p => p.RegisteredAt)
            .Select(p => p.Id)
            .ToListAsync(ct);
        return _cached;
    }

    /// <summary>The patients registered with the account's own number, without those it sees through a family link.</summary>
    public async Task<IReadOnlyList<Guid>> OwnNumberPatientIdsAsync(CancellationToken ct) =>
        await AccountPhoneAsync(ct) is { } phone
            ? await db.Set<Patient>()
                .Where(p => p.Phone == phone && p.IsActive && p.MergedIntoPatientId == null)
                .Select(p => p.Id)
                .ToListAsync(ct)
            : [];

    public async Task<bool> CanAccessAsync(Guid patientId, CancellationToken ct) =>
        (await AccessiblePatientIdsAsync(ct)).Contains(patientId);

    /// <summary>Returns a Forbidden error unless the portal user may see <paramref name="patientId"/>.</summary>
    public async Task<Result> EnsureAccessAsync(Guid patientId, CancellationToken ct) =>
        await CanAccessAsync(patientId, ct)
            ? Result.Success()
            : Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.");
}
