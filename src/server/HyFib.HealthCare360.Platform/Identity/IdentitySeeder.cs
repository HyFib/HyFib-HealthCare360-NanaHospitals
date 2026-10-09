using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// Seeds built-in roles, default permission grants (each grant applied once, so admin revocations survive
/// upgrades) and the first administrator. The initial admin password is random and written to a file in the
/// protected data directory unless a bootstrap password is configured, which only a development machine uses
/// (<see cref="Bootstrap"/>).
/// </summary>
internal sealed class IdentitySeeder(
    PermissionRegistry registry,
    PasswordHashing hashing,
    DataDirectories directories,
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<IdentitySeeder> logger) : ISeeder
{
    public const string AdminPasswordKey = "HealthCare360:Bootstrap:AdminPassword";
    public const string RequirePasswordChangeKey = "HealthCare360:Bootstrap:RequirePasswordChange";

    public int Order => 10;

    /// <summary>
    /// The first password and whether it must be changed (ASVS V6.3.2). A fixed password, keeping it, and the demonstration
    /// accounts that share it are for development machines only. Anywhere else the first administrator gets a random
    /// one-time password that must be changed at first sign-in, and no demonstration accounts are made.
    /// </summary>
    /// <returns>The configured password, or null for a random one; and whether the first sign-in must change it.</returns>
    internal static (string? Password, bool RequireChange) Bootstrap(IConfiguration configuration, bool isDevelopment)
    {
        if (!isDevelopment)
        {
            return (null, true);
        }

        var configured = configuration[AdminPasswordKey];
        return (string.IsNullOrWhiteSpace(configured) ? null : configured, configuration.GetValue(RequirePasswordChangeKey, true));
    }

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var bootstrap = Bootstrap(configuration, environment.IsDevelopment());
        if (!environment.IsDevelopment()
            && (!string.IsNullOrWhiteSpace(configuration[AdminPasswordKey]) || !configuration.GetValue(RequirePasswordChangeKey, true)))
        {
            logger.LogWarning("The bootstrap password settings ({Keys}) apply on development machines only, and are ignored here. Remove them.",
                $"{AdminPasswordKey}, {RequirePasswordChangeKey}");
        }
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
            await CreateInitialAdministratorAsync(db, superAdmin, bootstrap.Password, bootstrap.RequireChange, cancellationToken);
        }
        else if (await db.Set<User>().AnyAsync(
                     u => u.UserName == InitialAdminPassword.UserName && u.PasswordChangedAt != null, cancellationToken)
                 && InitialAdminPassword.Delete(directories))
        {
            // The administrator changed the password before this clean-up existed, or the file could not be deleted then.
            logger.LogInformation("Deleted the initial administrator's one-time password file: the password was changed");
        }

        // The demonstration accounts share the configured password, so they too are for development machines only.
        if (options.Value.SeedDemoData && bootstrap.Password is { } demoPassword)
        {
            await SeedDemoUsersAsync(db, roles, demoPassword, cancellationToken);
        }
    }

    private async Task CreateInitialAdministratorAsync(HospitalDbContext db, Role superAdmin, string? configured, bool requireChange, CancellationToken ct)
    {
        var password = configured ?? AccessAdminEndpoints.GenerateTemporaryPassword();
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

        if (configured is null)
        {
            var path = InitialAdminPassword.PathIn(directories);
            await File.WriteAllTextAsync(path,
                $"HyFib HealthCare360 — initial administrator{Environment.NewLine}User name: admin{Environment.NewLine}Password: {password}{Environment.NewLine}" +
                $"You will be asked to change this password at first sign-in. HC360 deletes this file once you have.{Environment.NewLine}", ct);
            // False positive: this logs where the file is, not the password. CodeQL takes the path for a password
            // because the file's name contains the word.
            // codeql[cs/cleartext-storage-of-sensitive-information]
            logger.LogWarning("Created the initial administrator 'admin'. The one-time password was written to {Path}", path);
        }
    }

    private async Task SeedDemoUsersAsync(HospitalDbContext db, Dictionary<string, Role> roles, string password, CancellationToken ct)
    {
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
