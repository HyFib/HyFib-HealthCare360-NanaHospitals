using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// Seeds built-in roles, default permission grants (each grant applied once, so admin revocations survive
/// upgrades) and the first administrator. The initial admin password is random and written to a file in the
/// protected data directory unless a bootstrap password is configured (development only).
/// </summary>
internal sealed class IdentitySeeder(
    PermissionRegistry registry,
    PasswordHashing hashing,
    DataDirectories directories,
    IConfiguration configuration,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<IdentitySeeder> logger) : ISeeder
{
    public int Order => 10;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var roles = await db.Set<Role>().Include(r => r.Permissions).ToDictionaryAsync(r => r.Name, StringComparer.Ordinal, cancellationToken);

        foreach (var (name, description) in SystemRoles.All)
        {
            if (!roles.ContainsKey(name))
            {
                var role = new Role { Name = name, Description = description, IsSystem = true };
                db.Add(role);
                roles[name] = role;
            }
        }

        var superAdmin = roles[SystemRoles.SuperAdmin];
        if (superAdmin.Permissions.All(p => p.Permission != PermissionRegistry.Wildcard))
        {
            superAdmin.Permissions.Add(new RolePermission { RoleId = superAdmin.Id, Permission = PermissionRegistry.Wildcard });
        }

        var seeded = (await db.Set<SeededGrant>().Select(s => s.RoleName + "|" + s.Permission).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var definition in registry.All.Where(d => d.DefaultRoles is { Length: > 0 }))
        {
            foreach (var roleName in definition.DefaultRoles!)
            {
                var key = roleName + "|" + definition.Name;
                if (seeded.Contains(key) || !roles.TryGetValue(roleName, out var role))
                {
                    continue;
                }

                if (role.Permissions.All(p => p.Permission != definition.Name))
                {
                    role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = definition.Name });
                }

                db.Add(new SeededGrant { RoleName = roleName, Permission = definition.Name, SeededAt = now });
                seeded.Add(key);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (!await db.Set<User>().AnyAsync(u => u.Type == UserType.Staff, cancellationToken))
        {
            await CreateInitialAdministratorAsync(db, superAdmin, cancellationToken);
        }

        if (options.Value.SeedDemoData)
        {
            await SeedDemoUsersAsync(db, roles, cancellationToken);
        }
    }

    private async Task CreateInitialAdministratorAsync(HospitalDbContext db, Role superAdmin, CancellationToken ct)
    {
        var configured = configuration["HealthCare360:Bootstrap:AdminPassword"];
        var password = string.IsNullOrWhiteSpace(configured) ? AccessAdminEndpoints.GenerateTemporaryPassword() : configured;
        var requireChange = configuration.GetValue("HealthCare360:Bootstrap:RequirePasswordChange", true);
        var facilityId = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);

        var admin = new User
        {
            UserName = "admin",
            NormalizedUserName = "admin",
            DisplayName = "System Administrator",
            Type = UserType.Staff,
            PasswordHash = hashing.Hash(password),
            MustChangePassword = requireChange,
            DefaultFacilityId = facilityId,
        };
        admin.Roles.Add(new UserRole { UserId = admin.Id, RoleId = superAdmin.Id });
        if (facilityId is { } fid)
        {
            admin.Facilities.Add(new UserFacility { UserId = admin.Id, FacilityId = fid });
        }

        db.Add(admin);
        await db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.Combine(directories.Root, "initial-admin-password.txt");
            await File.WriteAllTextAsync(path,
                $"HyFib HealthCare360 — initial administrator{Environment.NewLine}User name: admin{Environment.NewLine}Password: {password}{Environment.NewLine}" +
                $"You will be asked to change this password at first sign-in. Delete this file afterwards.{Environment.NewLine}", ct);
            logger.LogWarning("Created the initial administrator 'admin'. The one-time password was written to {Path}", path);
        }
    }

    private async Task SeedDemoUsersAsync(HospitalDbContext db, Dictionary<string, Role> roles, CancellationToken ct)
    {
        var password = configuration["HealthCare360:Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var facilityId = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
        var doctor = await db.Set<Practitioner>().Where(p => p.Kind == PractitionerKind.Consultant).OrderBy(p => p.Code).FirstOrDefaultAsync(ct);

        var demo = new (string UserName, string DisplayName, string Role, Guid? PractitionerId)[]
        {
            ("doctor", doctor?.DisplayName ?? "Dr. Demo Doctor", SystemRoles.Doctor, doctor?.Id),
            ("nurse", "Nurse Priya", SystemRoles.Nurse, null),
            ("frontoffice", "Front Office Desk", SystemRoles.FrontOffice, null),
            ("billing", "Billing Counter", SystemRoles.Billing, null),
            ("pharmacist", "Pharmacist Ravi", SystemRoles.Pharmacist, null),
            ("lab", "Lab Technician Anu", SystemRoles.LabTechnician, null),
            ("hospitaladmin", "Hospital Administrator", SystemRoles.HospitalAdmin, null),
        };

        foreach (var (userName, displayName, roleName, practitionerId) in demo)
        {
            if (await db.Set<User>().AnyAsync(u => u.NormalizedUserName == userName, ct) || !roles.TryGetValue(roleName, out var role))
            {
                continue;
            }

            var user = new User
            {
                UserName = userName,
                NormalizedUserName = userName,
                DisplayName = displayName,
                Type = UserType.Staff,
                PasswordHash = hashing.Hash(password),
                PractitionerId = practitionerId,
                DefaultFacilityId = facilityId,
            };
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            if (facilityId is { } fid)
            {
                user.Facilities.Add(new UserFacility { UserId = user.Id, FacilityId = fid });
            }

            db.Add(user);
        }

        await db.SaveChangesAsync(ct);
    }
}
