using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Configuration;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// ASVS V6.3.1, V6.3.2, V6.3.3: who must use two-step verification, how wrong passwords and codes lock an account and are
/// forgotten, and that a fixed first password is for development machines only.
/// </summary>
public sealed class SignInPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SystemRoles.SuperAdmin)]
    [InlineData(SystemRoles.HospitalAdmin)]
    [InlineData(SystemRoles.Accountant)]
    [InlineData(SystemRoles.HrManager)]
    [InlineData("accountant")]
    public void The_administrators_and_those_handling_money_must_use_two_step_verification(string role) =>
        SignInPolicy.RequiresMfa(new SecurityOptions(), [role], []).ShouldBeTrue();

    [Fact]
    public void Clinical_and_desk_roles_need_not()
    {
        SignInPolicy.RequiresMfa(new SecurityOptions(), [SystemRoles.Doctor, SystemRoles.Billing], ["clinical.notes.write", "billing.bills.manage"])
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData(PlatformPermissions.AuditRead)]
    [InlineData(PlatformPermissions.UsersManage)]
    [InlineData(PermissionRegistry.Wildcard)]
    public void So_must_anyone_holding_an_administrative_permission_whatever_the_role(string permission) =>
        SignInPolicy.RequiresMfa(new SecurityOptions(), ["Night supervisors"], ["clinical.notes.write", permission]).ShouldBeTrue();

    [Fact]
    public void Both_can_be_turned_off()
    {
        SignInPolicy.RequiresMfa(new SecurityOptions { MfaForAdministration = false }, ["Night supervisors"], [PlatformPermissions.UsersManage])
            .ShouldBeFalse();
        SignInPolicy.RequiresMfa(new SecurityOptions { EnforceMfa = false }, [SystemRoles.SuperAdmin], [PermissionRegistry.Wildcard])
            .ShouldBeFalse();
    }

    [Fact]
    public void Every_fifth_failure_in_a_row_locks_the_account_for_twice_as_long_up_to_a_day()
    {
        var security = new SecurityOptions();
        var user = new User();

        var locks = Enumerable.Range(1, 50).Select(i =>
        {
            var at = Now.AddMinutes(i);
            return SignInPolicy.RecordFailure(security, user, at) ? user.LockoutEndsAt - at : null;
        }).ToList();

        locks.Take(4).ShouldAllBe(l => l == null);
        locks[4].ShouldBe(TimeSpan.FromMinutes(15));
        locks[9].ShouldBe(TimeSpan.FromMinutes(30));
        locks[14].ShouldBe(TimeSpan.FromMinutes(60));
        locks[49].ShouldBe(TimeSpan.FromHours(24));
        user.FailedLoginCount.ShouldBe(50);
    }

    [Fact]
    public void Failures_are_forgotten_after_a_day_without_another()
    {
        var security = new SecurityOptions();
        var user = new User { FailedLoginCount = 9, LastFailedLoginAt = Now.AddHours(-24) };

        SignInPolicy.RecordFailure(security, user, Now).ShouldBeFalse();

        (user.FailedLoginCount, user.LastFailedLoginAt).ShouldBe((1, Now));
        user.LockoutEndsAt.ShouldBeNull();
    }

    [Fact]
    public void Failures_within_the_day_add_up()
    {
        var security = new SecurityOptions();
        var user = new User { FailedLoginCount = 9, LastFailedLoginAt = Now.AddHours(-23) };

        SignInPolicy.RecordFailure(security, user, Now).ShouldBeTrue();

        user.FailedLoginCount.ShouldBe(10);
        user.LockoutEndsAt.ShouldBe(Now.AddMinutes(30));
    }

    [Fact]
    public void With_no_reset_period_failures_are_never_forgotten()
    {
        var user = new User { FailedLoginCount = 4, LastFailedLoginAt = Now.AddDays(-30) };

        SignInPolicy.RecordFailure(new SecurityOptions { FailedLoginResetHours = 0 }, user, Now).ShouldBeTrue();

        user.FailedLoginCount.ShouldBe(5);
    }

    [Fact]
    public void A_fixed_first_password_and_keeping_it_are_for_development_machines_only()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IdentitySeeder.AdminPasswordKey] = "Fixed-Password-2026",
            [IdentitySeeder.RequirePasswordChangeKey] = "false",
        }).Build();

        IdentitySeeder.Bootstrap(configuration, isDevelopment: true).ShouldBe(("Fixed-Password-2026", false));
        IdentitySeeder.Bootstrap(configuration, isDevelopment: false).ShouldBe(((string?)null, true));
        IdentitySeeder.Bootstrap(new ConfigurationBuilder().Build(), isDevelopment: true).ShouldBe(((string?)null, true));
    }
}
