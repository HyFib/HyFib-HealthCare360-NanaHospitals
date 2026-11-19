using HyFib.HealthCare360.Modules.Inpatient.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class InpatientModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new InpatientModule().Name.ShouldBe("inpatient");

    [Fact]
    public void Permissions_are_unique_and_use_the_module_prefixes()
    {
        var names = new InpatientModule().Permissions.Select(p => p.Name).ToList();

        names.ShouldBeUnique();
        names.ShouldAllBe(n => n.StartsWith("inpatient.") || n.StartsWith("beds.") || n.StartsWith("nursing."));
        names.ShouldContain(InpatientPermissions.Admit);
        names.ShouldContain(InpatientPermissions.NursingMar);
    }

    [Fact]
    public void Every_permission_is_granted_to_at_least_one_role_by_default() =>
        new InpatientModule().Permissions.ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0);

    [Fact]
    public void The_job_that_keeps_the_medication_chart_a_day_ahead_runs_with_the_module()
    {
        var services = new ServiceCollection();
        new InpatientModule().AddServices(services, new ConfigurationBuilder().Build());

        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MarChartAheadJob));
        services.ShouldContain(d => d.ServiceType == typeof(IMarChartAhead));
    }
}
