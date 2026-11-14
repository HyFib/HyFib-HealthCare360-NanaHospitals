using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// The accounts a record-sharing consent can name: active accounts given <see cref="InteropPermissions.FhirRead"/> by a
/// role the hospital made for them (the "FHIR integration" role of the set-up), while that role is in use. The built-in
/// roles that hold it, such as the hospital administrator's, are staff, not other care providers, so their accounts are
/// not offered.
/// </summary>
internal sealed class SharingPartnerDirectory(HospitalDbContext db) : ISharingPartnerDirectory
{
    public async Task<IReadOnlyList<SharingPartner>> ListAsync(CancellationToken ct)
    {
        var readerRoles = db.Set<Role>().Where(r => !r.IsSystem && r.IsActive && r.Permissions.Any(p => p.Permission == InteropPermissions.FhirRead)).Select(r => r.Id);
        return await db.Set<User>().AsNoTracking()
            .Where(u => u.IsActive && u.Type != UserType.Patient && u.Roles.Any(ur => readerRoles.Contains(ur.RoleId)))
            .OrderBy(u => u.DisplayName)
            .Select(u => new SharingPartner(u.Id, u.DisplayName))
            .ToListAsync(ct);
    }
}
