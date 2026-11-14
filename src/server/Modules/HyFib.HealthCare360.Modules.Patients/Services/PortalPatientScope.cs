using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Which patients a Patient Portal user may see: the active patients registered with the mobile number the user verified
/// by OTP (families in India commonly share one number) whose number is known to reach them, and the family members on
/// other numbers that a <see cref="PortalFamilyLink"/> in force gives the account (PP-01). Portal endpoints in any module
/// must call <see cref="CanAccessAsync"/> (or filter with <see cref="AccessiblePatientIdsAsync"/>) before returning data.
/// <para>
/// Holding the number is not enough on its own (ASVS V6.6.1): the desk may have typed a digit wrong, or the telco may have
/// given a disconnected number to someone new. So a record on the number is shown once its number is verified
/// (<see cref="Patient.IsPhoneVerified"/>: a code at the desk, a number change with a code, or the portal itself). Until then
/// it waits for the account to give the patient's date of birth or UHID (<see cref="UnconfirmedPatientIdsAsync"/>,
/// <see cref="PortalRecordConfirmationService"/>), which verifies it. Family links are made with a code sent to the
/// patient's own number and are shown as before.
/// </para>
/// </summary>
public sealed class PortalPatientScope(HospitalDbContext db, ICurrentUser currentUser)
{
    private IReadOnlyList<Reached>? _reached;
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

    /// <summary>
    /// The account's patients: those on its own number first, then linked family members, each oldest first. A record on the
    /// account's own number whose number is not verified waits to be confirmed (<see cref="UnconfirmedPatientIdsAsync"/>). A
    /// record in a protected category is left out when another patient shares the number, or when it is reached only through
    /// a family link (<see cref="SharedNumbers"/>).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AccessiblePatientIdsAsync(CancellationToken ct) =>
        _cached ??= await AccountPhoneAsync(ct) is { } phone ? Shown(await ReachedAsync(phone, ct)) : [];

    /// <summary>
    /// The records on the account's own number that it would see once it confirms them, oldest first: their number is not
    /// verified, so the account first gives the patient's date of birth or UHID. A record the account could not see anyway
    /// (a protected category on a shared number) is not offered.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> UnconfirmedPatientIdsAsync(CancellationToken ct) =>
        await AccountPhoneAsync(ct) is { } phone ? Waiting(await ReachedAsync(phone, ct)) : [];

    /// <summary>
    /// The patients the portal account of <paramref name="phone"/> reaches: the active records registered with the number,
    /// and those a family link in force gives it, before any is hidden (<see cref="SharedNumbers"/>) or waits to be confirmed.
    /// None means the number reaches nobody's records any more (a change of a patient's number, for one, ends the old
    /// number's sessions then: <see cref="PatientPhoneChangeService"/>).
    /// </summary>
    internal static IQueryable<Patient> ReachedBy(HospitalDbContext db, string phone)
    {
        var linked = db.Set<PortalFamilyLink>().Where(l => l.AccountPhone == phone && l.RevokedAt == null).Select(l => l.PatientId);
        return db.Set<Patient>().Where(p => (p.Phone == phone || linked.Contains(p.Id)) && p.IsActive && p.MergedIntoPatientId == null);
    }

    /// <summary>
    /// The patients registered with the account's own number whose number is verified, without those it sees through a
    /// family link, and without a record in a protected category while the number is shared.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> OwnNumberPatientIdsAsync(CancellationToken ct) =>
        await AccountPhoneAsync(ct) is { } phone
            ? Shown(await db.Set<Patient>()
                .Where(p => p.Phone == phone && p.IsActive && p.MergedIntoPatientId == null)
                .Select(p => new Reached(p.Id, true, false, p.PhoneVerifiedAt != null && p.PhoneVerifiedNumber == p.Phone, p.SensitiveCategories))
                .ToListAsync(ct))
            : [];

    /// <summary>
    /// Whether signing in with the account's number identifies <paramref name="patientId"/> as the person acting: the record
    /// is registered with the account's own number, that number is verified, and no other active patient has it. A number
    /// a family shares says only that someone in the family holds the phone, not which patient is asking, so a request made
    /// from it is not taken as checked (even when every record on it is verified).
    /// </summary>
    public async Task<bool> SignInIdentifiesAsync(Guid patientId, CancellationToken ct) =>
        await AccountPhoneAsync(ct) is { } phone
        && Identifies(
            await db.Set<Patient>()
                .Where(p => p.Phone == phone && p.IsActive && p.MergedIntoPatientId == null)
                .Select(p => new Reached(p.Id, true, false, p.PhoneVerifiedAt != null && p.PhoneVerifiedNumber == p.Phone, p.SensitiveCategories))
                .ToListAsync(ct),
            patientId);

    /// <summary>The rule of <see cref="SignInIdentifiesAsync"/> over the records on the account's own number.</summary>
    internal static bool Identifies(IReadOnlyList<Reached> ownNumber, Guid patientId) =>
        ownNumber.Count == 1 && ownNumber[0].Id == patientId && ownNumber[0].Verified;

    private async Task<IReadOnlyList<Reached>> ReachedAsync(string phone, CancellationToken ct)
    {
        if (_reached is not null)
        {
            return _reached;
        }

        // Verified as Patient.IsPhoneVerified says: the record's number was verified, and is still the number verified.
        var linked = db.Set<PortalFamilyLink>().Where(l => l.AccountPhone == phone && l.RevokedAt == null).Select(l => l.PatientId);
        return _reached = await ReachedBy(db, phone)
            .OrderBy(p => p.Phone == phone ? 0 : 1)
            .ThenBy(p => p.RegisteredAt)
            .Select(p => new Reached(
                p.Id, p.Phone == phone, linked.Contains(p.Id), p.PhoneVerifiedAt != null && p.PhoneVerifiedNumber == p.Phone, p.SensitiveCategories))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The records shown to the account: one on its own number once that number is verified, one a family link gives it as
    /// before; and of those, a record in a protected category only on the patient's own number while no other patient has
    /// it. The number counts as shared whether or not the other records on it are verified: any of them may be the phone's.
    /// </summary>
    internal static List<Guid> Shown(IReadOnlyList<Reached> reached)
    {
        var numberShared = reached.Count(r => r.OwnNumber) > 1;
        return [.. reached
            .Where(r => (r.Linked || (r.OwnNumber && r.Verified)) && SharedNumbers.ShownToAccount(r.OwnNumber, numberShared, r.Categories))
            .Select(r => r.Id)];
    }

    /// <summary>The records on the account's own number it would be shown once their number is verified.</summary>
    internal static List<Guid> Waiting(IReadOnlyList<Reached> reached)
    {
        var numberShared = reached.Count(r => r.OwnNumber) > 1;
        return [.. reached
            .Where(r => r.OwnNumber && !r.Verified && !r.Linked && SharedNumbers.ShownToAccount(true, numberShared, r.Categories))
            .Select(r => r.Id)];
    }

    /// <param name="OwnNumber">Registered with the account's number.</param>
    /// <param name="Linked">A family link in force gives the account the record.</param>
    /// <param name="Verified">The record's number is verified (<see cref="Patient.IsPhoneVerified"/>).</param>
    internal sealed record Reached(Guid Id, bool OwnNumber, bool Linked, bool Verified, SensitiveCategory Categories);

    public async Task<bool> CanAccessAsync(Guid patientId, CancellationToken ct) =>
        (await AccessiblePatientIdsAsync(ct)).Contains(patientId);

    /// <summary>Returns a Forbidden error unless the portal user may see <paramref name="patientId"/>.</summary>
    public async Task<Result> EnsureAccessAsync(Guid patientId, CancellationToken ct) =>
        await CanAccessAsync(patientId, ct)
            ? Result.Success()
            : Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.");
}
