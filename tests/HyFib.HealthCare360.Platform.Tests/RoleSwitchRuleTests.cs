using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Who may turn a role on or off, row by row: never the Super Administrator's role, which is how the hospital is
/// administered, nor the Patient role, which every portal account holds; and nobody turns off a role they hold, which would
/// take their own access with nobody to give it back. Turning on a role one holds is not refused here: it waits for someone
/// else, as access given to oneself does (GrantDecisionTests). The integration tests (RoleSwitchTests) drive the endpoint;
/// in the seeded hospital only the Super Administrator may switch roles, so the row of a role one holds is here.
/// </summary>
public sealed class RoleSwitchRuleTests
{
    [Theory]
    [InlineData(SystemRoles.SuperAdmin, false, false)]
    [InlineData(SystemRoles.SuperAdmin, false, true)]
    [InlineData(SystemRoles.SuperAdmin, true, true)]
    [InlineData(SystemRoles.Patient, false, false)]
    [InlineData(SystemRoles.Patient, true, false)]
    public void The_Super_Administrator_and_Patient_roles_are_never_switched(string role, bool on, bool holds)
    {
        var refused = AccessAdminEndpoints.RoleSwitchRefusal(role, on, holds).ShouldNotBeNull();

        (refused.Code, refused.Type).ShouldBe(("role.protected", ErrorType.BusinessRule));
    }

    [Fact]
    public void The_Patient_role_points_to_the_patient_portals_switch()
    {
        AccessAdminEndpoints.RoleSwitchRefusal(SystemRoles.Patient, on: false, holds: false)!.Message.ShouldContain("patient portal");
    }

    [Theory]
    [InlineData(SystemRoles.HospitalAdmin)]
    [InlineData(SystemRoles.Pharmacist)]
    [InlineData("Theatre list")]
    public void Nobody_turns_off_a_role_they_hold(string role)
    {
        var refused = AccessAdminEndpoints.RoleSwitchRefusal(role, on: false, holds: true).ShouldNotBeNull();

        (refused.Code, refused.Type).ShouldBe(("role.self_deactivate", ErrorType.BusinessRule));
    }

    [Theory]
    [InlineData(SystemRoles.HospitalAdmin, false, false)]
    [InlineData(SystemRoles.Pharmacist, true, false)]
    [InlineData(SystemRoles.Pharmacist, true, true)]
    [InlineData("Theatre list", false, false)]
    public void Any_other_role_may_be_switched_and_one_held_may_be_asked_back(string role, bool on, bool holds)
    {
        AccessAdminEndpoints.RoleSwitchRefusal(role, on, holds).ShouldBeNull();
    }

    [Fact]
    public void Every_built_in_role_but_those_always_in_use_may_be_turned_off()
    {
        SystemRoles.All.Select(r => r.Name)
            .Where(name => AccessAdminEndpoints.RoleSwitchRefusal(name, on: false, holds: false) is not null)
            .ShouldBe(SystemRoles.AlwaysInUse, ignoreOrder: true, "the roles page and the role list say which roles are always in use from this list");
        SystemRoles.AlwaysInUse.ShouldBe([SystemRoles.SuperAdmin, SystemRoles.Patient], ignoreOrder: true);
    }
}
