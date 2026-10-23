using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// Staff accounts for a load test (docs/testing/LOAD-TESTS.md): loadtest01, loadtest02, … with the Front Office role and
/// one password. The demonstration accounts get their password only on development machines (<see cref="IdentitySeeder"/>),
/// and a test server runs as in production, so its load test needs accounts of its own, made with
/// <c>HyFib.HealthCare360.Server.exe loadtest accounts --count N</c>. An account made before takes the new password, so a
/// run can always sign in. Only on a server set up with demonstration data, as the synthetic patients.
/// </summary>
public sealed class LoadTestAccounts(HospitalDbContext db, PasswordHashing hashing, IOptions<PlatformOptions> options)
{
    public const string UserNamePrefix = "loadtest";

    public const int MaxAccounts = 200;

    public const int MinPasswordLength = 12;

    public static string UserName(int number) => UserNamePrefix + number.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>Makes or updates accounts 1 to <paramref name="count"/>, and returns their user names.</summary>
    public async Task<Result<IReadOnlyList<string>>> EnsureAsync(int count, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!options.Value.SeedDemoData)
        {
            return Error.Rule("loadtest.not_demo",
                "Load test accounts are made only on a server set up with demonstration data (HealthCare360:SeedDemoData), never on a hospital's live server.");
        }

        if (count is < 1 or > MaxAccounts)
        {
            return Error.Field("count", $"Give a number of accounts from 1 to {MaxAccounts}.");
        }

        if (password.Length is < MinPasswordLength or > PasswordPolicy.MaxLength)
        {
            return Error.Field("password", $"Give the accounts a password of {MinPasswordLength} to {PasswordPolicy.MaxLength} characters.");
        }

        var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Name == SystemRoles.FrontOffice, cancellationToken);
        var facilityId = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken);
        if (role is null || facilityId is not { } facility)
        {
            return Error.Rule("loadtest.not_set_up", "The server is not set up yet: start it once so it makes its roles and facility.");
        }

        var names = Enumerable.Range(1, count).Select(UserName).ToList();
        var existing = await db.Set<User>().Where(u => names.Contains(u.NormalizedUserName)).ToDictionaryAsync(u => u.NormalizedUserName, StringComparer.Ordinal, cancellationToken);
        var hash = hashing.Hash(password);
        foreach (var name in names)
        {
            if (existing.TryGetValue(name, out var user))
            {
                user.PasswordHash = hash;
                user.MustChangePassword = false;
                user.IsActive = true;
                user.FailedLoginCount = 0;
                user.LockoutEndsAt = null;
                continue;
            }

            user = new User
            {
                UserName = name,
                NormalizedUserName = name,
                DisplayName = "Load test " + name[UserNamePrefix.Length..],
                Type = UserType.Staff,
                PasswordHash = hash,
                DefaultFacilityId = facility,
            };
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            user.Facilities.Add(new UserFacility { UserId = user.Id, FacilityId = facility });
            db.Add(user);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result<IReadOnlyList<string>>.Ok(names);
    }
}
