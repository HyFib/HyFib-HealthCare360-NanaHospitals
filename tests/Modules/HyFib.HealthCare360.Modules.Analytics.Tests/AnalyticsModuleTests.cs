using HyFib.HealthCare360.Modules.Analytics.Reports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Modules.Analytics.Tests;

public sealed class AnalyticsModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new AnalyticsModule().Name.ShouldBe("analytics");

    [Fact]
    public void The_server_s_report_catalogue_has_every_report()
    {
        var services = new ServiceCollection();
        new AnalyticsModule().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<ReportCatalog>();

        catalog.All.Count.ShouldBe(new ReportCatalog().All.Count);
        catalog.Find("revenue-by-department").ShouldNotBeNull();
    }
}
