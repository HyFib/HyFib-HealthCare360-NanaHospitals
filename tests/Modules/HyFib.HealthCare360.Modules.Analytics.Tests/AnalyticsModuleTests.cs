namespace HyFib.HealthCare360.Modules.Analytics.Tests;

public sealed class AnalyticsModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new AnalyticsModule().Name.ShouldBe("analytics");
}
