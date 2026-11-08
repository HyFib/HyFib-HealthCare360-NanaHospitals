using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class QualityModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new QualityModule().Name.ShouldBe("quality");

    [Fact]
    public void Every_staff_member_can_report_incidents()
    {
        var report = new QualityModule().Permissions.Single(p => p.Name == QualityPermissions.IncidentsReport);

        report.DefaultRoles!.ShouldContain(SystemRoles.Nurse);
        report.DefaultRoles!.ShouldContain(SystemRoles.Doctor);
        report.DefaultRoles!.ShouldNotContain(SystemRoles.Patient);
    }

    [Fact]
    public void Management_permissions_default_to_quality_managers_and_admins()
    {
        var managed = new QualityModule().Permissions.Where(p => p.Name is not (QualityPermissions.IncidentsReport or QualityPermissions.HaiDevices)).ToList();

        managed.Select(p => p.Name).ShouldBe(
            [
                QualityPermissions.IncidentsManage, QualityPermissions.CapaManage, QualityPermissions.RisksManage, QualityPermissions.IndicatorsManage,
                QualityPermissions.DowntimeManage, QualityPermissions.HaiManage, QualityPermissions.AuditsManage,
            ]);
        managed.ShouldAllBe(p => p.DefaultRoles!.SequenceEqual(new[] { SystemRoles.QualityManager, SystemRoles.HospitalAdmin }));
    }

    [Fact]
    public void Nurses_and_doctors_record_devices_for_infection_surveillance()
    {
        var devices = new QualityModule().Permissions.Single(p => p.Name == QualityPermissions.HaiDevices);

        devices.DefaultRoles!.ShouldContain(SystemRoles.Nurse);
        devices.DefaultRoles!.ShouldContain(SystemRoles.Doctor);
        devices.DefaultRoles!.ShouldNotContain(SystemRoles.Patient);
    }
}
