using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

public sealed class SchedulingModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new SchedulingModule().Name.ShouldBe("scheduling");

    [Fact]
    public void Permissions_are_namespaced_and_unique()
    {
        var names = new SchedulingModule().Permissions.Select(p => p.Name).ToList();

        names.ShouldAllBe(name => name.StartsWith("scheduling.", StringComparison.Ordinal));
        names.Distinct().Count().ShouldBe(names.Count);
        names.ShouldBe(
            [SchedulingPermissions.SchedulesManage, SchedulingPermissions.AppointmentsRead, SchedulingPermissions.AppointmentsBook, SchedulingPermissions.AppointmentsManage],
            ignoreOrder: true);
    }

    [Fact]
    public void Default_roles_are_built_in_roles_and_exclude_patients()
    {
        var builtIn = SystemRoles.All.Select(r => r.Name).ToHashSet();

        foreach (var permission in new SchedulingModule().Permissions)
        {
            permission.DefaultRoles.ShouldNotBeNull();
            permission.DefaultRoles.ShouldAllBe(role => builtIn.Contains(role));
            permission.DefaultRoles.ShouldNotContain(SystemRoles.Patient);
        }
    }

    [Fact]
    public void Front_office_can_run_the_appointment_desk()
    {
        var granted = new SchedulingModule().Permissions.Where(p => p.DefaultRoles!.Contains(SystemRoles.FrontOffice)).Select(p => p.Name).ToList();

        granted.ShouldContain(SchedulingPermissions.AppointmentsBook);
        granted.ShouldContain(SchedulingPermissions.AppointmentsManage);
        granted.ShouldContain(SchedulingPermissions.SchedulesManage);
    }
}
