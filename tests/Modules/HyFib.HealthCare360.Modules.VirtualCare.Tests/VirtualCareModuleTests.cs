using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class VirtualCareModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new VirtualCareModule().Name.ShouldBe("virtualcare");

    [Fact]
    public void Permissions_are_namespaced_and_unique()
    {
        var names = new VirtualCareModule().Permissions.Select(p => p.Name).ToList();

        names.ShouldAllBe(name => name.StartsWith("telehealth.", StringComparison.Ordinal) || name.StartsWith("rpm.", StringComparison.Ordinal));
        names.Distinct().Count().ShouldBe(names.Count);
        names.Count.ShouldBe(6);
    }

    [Fact]
    public void Default_roles_are_built_in_roles_and_exclude_patients()
    {
        var builtIn = SystemRoles.All.Select(r => r.Name).ToHashSet();

        foreach (var permission in new VirtualCareModule().Permissions)
        {
            permission.DefaultRoles.ShouldNotBeNull();
            permission.DefaultRoles.ShouldAllBe(role => builtIn.Contains(role));
            permission.DefaultRoles.ShouldNotContain(SystemRoles.Patient);
        }
    }

    [Fact]
    public void Doctors_consult_and_nurses_handle_monitoring_alerts()
    {
        var permissions = new VirtualCareModule().Permissions.ToDictionary(p => p.Name, p => p.DefaultRoles!);

        permissions[VirtualCarePermissions.TelehealthConsult].ShouldBe([SystemRoles.Doctor]);
        permissions[VirtualCarePermissions.RpmAlerts].ShouldContain(SystemRoles.Nurse);
        permissions[VirtualCarePermissions.RpmAlerts].ShouldContain(SystemRoles.Doctor);
    }
}
