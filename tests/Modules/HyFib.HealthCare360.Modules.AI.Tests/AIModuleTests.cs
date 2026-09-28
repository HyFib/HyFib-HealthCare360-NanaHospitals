using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Assistant;
using HyFib.HealthCare360.Modules.AI.Services.Clinical;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class AIModuleTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        new AIModule().AddServices(services, new ConfigurationBuilder().Build());
        return services;
    }

    [Fact]
    public void Module_uses_its_schema_name_as_key() => new AIModule().Name.ShouldBe("ai");

    [Theory]
    [InlineData(AIPermissions.AssistantUse, new[] { SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.FrontOffice, SystemRoles.HospitalAdmin })]
    [InlineData(AIPermissions.DictationUse, new[] { SystemRoles.Doctor, SystemRoles.HospitalAdmin })]
    [InlineData(AIPermissions.SettingsManage, new[] { SystemRoles.HospitalAdmin })]
    public void Grants_each_permission_to_the_roles_that_need_it(string permission, string[] roles) =>
        new AIModule().Permissions.Single(p => p.Name == permission).DefaultRoles.ShouldBe(roles, ignoreOrder: true);

    [Theory]
    [InlineData(typeof(IAiTextService))]
    [InlineData(typeof(ISpeechToText))]
    [InlineData(typeof(IAiGateway))]
    [InlineData(typeof(AssistantCommandService))]
    [InlineData(typeof(ClinicalNoteStructurer))]
    public void Registers_its_services_once(Type service) => Services().Count(d => d.ServiceType == service).ShouldBe(1);

    [Fact]
    public void A_platform_fallback_added_later_does_not_replace_the_router()
    {
        var services = Services();

        services.TryAddScoped<IAiTextService, DisabledAiTextService>();

        services.Single(d => d.ServiceType == typeof(IAiTextService)).ImplementationType.ShouldBeNull();
    }
}
