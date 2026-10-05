using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

public sealed class DiagnosticsModuleTests
{
    private static readonly IReadOnlyList<PermissionDefinition> Permissions = new DiagnosticsModule().Permissions.ToList();

    [Fact]
    public void Module_uses_its_schema_name_as_key() => new DiagnosticsModule().Name.ShouldBe("diagnostics");

    [Theory]
    [InlineData("lab.orders.read")]
    [InlineData("lab.samples.collect")]
    [InlineData("lab.samples.receive")]
    [InlineData("lab.results.enter")]
    [InlineData("lab.results.verify")]
    [InlineData("lab.qc.manage")]
    [InlineData("lab.masters.manage")]
    [InlineData("radiology.worklist")]
    [InlineData("radiology.reports.write")]
    [InlineData("radiology.reports.verify")]
    [InlineData("cardiology.studies.manage")]
    [InlineData("cardiology.reports.verify")]
    public void The_specified_permissions_are_declared(string name) => Permissions.ShouldContain(p => p.Name == name);

    [Fact]
    public void Permissions_are_unique_grouped_and_granted_to_default_roles()
    {
        Permissions.Select(p => p.Name).ShouldBeUnique();
        Permissions.ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0 && p.Group.Length > 0 && p.Description.Length > 0);
    }

    [Theory]
    [InlineData(DiagnosticsPermissions.LabSamplesCollect, SystemRoles.Nurse)]
    [InlineData(DiagnosticsPermissions.LabSamplesCollect, SystemRoles.LabTechnician)]
    [InlineData(DiagnosticsPermissions.LabResultsEnter, SystemRoles.LabTechnician)]
    [InlineData(DiagnosticsPermissions.LabResultsVerify, SystemRoles.Pathologist)]
    [InlineData(DiagnosticsPermissions.LabOrdersRead, SystemRoles.Doctor)]
    [InlineData(DiagnosticsPermissions.RadiologyWorklist, SystemRoles.Radiographer)]
    [InlineData(DiagnosticsPermissions.RadiologyReportsVerify, SystemRoles.Radiologist)]
    [InlineData(DiagnosticsPermissions.RadiologyRead, SystemRoles.Doctor)]
    [InlineData(DiagnosticsPermissions.CardiologyStudiesManage, SystemRoles.CardiologyTechnician)]
    [InlineData(DiagnosticsPermissions.CardiologyRead, SystemRoles.Doctor)]
    [InlineData(DiagnosticsPermissions.CardiologyRead, SystemRoles.Cardiologist)]
    [InlineData(DiagnosticsPermissions.CriticalAlerts, SystemRoles.Doctor)]
    public void Default_roles_match_the_people_doing_the_work(string permission, string role) =>
        Permissions.Single(p => p.Name == permission).DefaultRoles!.ShouldContain(role);

    [Fact]
    public void Only_pathologists_verify_lab_results_by_default() =>
        Permissions.Single(p => p.Name == DiagnosticsPermissions.LabResultsVerify).DefaultRoles.ShouldBe([SystemRoles.Pathologist]);

    [Fact]
    public void Only_cardiologists_sign_off_cardiology_reports_by_default() =>
        Permissions.Single(p => p.Name == DiagnosticsPermissions.CardiologyReportsVerify).DefaultRoles.ShouldBe([SystemRoles.Cardiologist]);

    [Fact]
    public void The_analyser_gateway_can_resolve_the_lab_result_sink()
    {
        var services = new ServiceCollection();

        new DiagnosticsModule().AddServices(services, new ConfigurationBuilder().Build());

        services.ShouldContain(d => d.ServiceType == typeof(IInstrumentResultSink) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void Doctors_radiographers_and_radiologists_see_images_and_nurses_read_the_report()
    {
        var images = Permissions.Single(p => p.Name == DiagnosticsPermissions.RadiologyImages);

        images.Name.ShouldBe("radiology.images.view");
        images.DefaultRoles.ShouldBe([SystemRoles.Doctor, SystemRoles.Radiographer, SystemRoles.Radiologist], ignoreOrder: true);
        Permissions.Single(p => p.Name == DiagnosticsPermissions.RadiologyRead).DefaultRoles!.ShouldContain(SystemRoles.Nurse);
    }

    [Fact]
    public void The_worklist_server_can_resolve_the_worklist_and_studies_are_linked_on_a_schedule()
    {
        var services = new ServiceCollection();

        new DiagnosticsModule().AddServices(services, new ConfigurationBuilder().Build());

        services.ShouldContain(d => d.ServiceType == typeof(IModalityWorklistSource) && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(Services.PacsLinkJob));
    }
}
