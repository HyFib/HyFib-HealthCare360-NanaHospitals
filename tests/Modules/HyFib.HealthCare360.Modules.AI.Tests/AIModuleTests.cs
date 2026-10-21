using System.Net;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Assistant;
using HyFib.HealthCare360.Modules.AI.Services.Clinical;
using HyFib.HealthCare360.Modules.AI.Services.OpenAi;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Http;
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
    [InlineData(typeof(AiTextProviderRegistry))]
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

    [Fact]
    public async Task A_post_to_the_ai_server_is_never_retried()
    {
        var server = new StubHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, "{}")
            .Respond(HttpStatusCode.OK, OpenAiCompatibleTextServiceTests.ChatCompletion("{}"));
        var services = Services();
        services.AddLogging();
        services.AddTransient<CorrelationIdForwardingHandler>();
        services.AddHttpClient<OpenAiCompatibleTextService>().ConfigurePrimaryHttpMessageHandler(() => server);
        await using var root = services.BuildServiceProvider();
        await using var scope = root.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetServices<IAiTextProvider>().Single(p => p.Key == AiProviders.OpenAi);

        var completion = await provider.CompleteAsync(
            RedactedAiRequest.Redact(AiPrompts.StructureNote("fever for two days", null), RedactionContext.Empty),
            new AiTextSettings(true, AiProviders.OpenAi, "local-model", null, TimeSpan.FromSeconds(30), new Uri("https://llm.hospital.lan/v1/chat/completions")),
            TestContext.Current.CancellationToken);

        completion.Error.ShouldBe("The AI service is temporarily unavailable.");
        server.Requests.Count.ShouldBe(1, "The server may already be working on the request; a retry would run it twice.");
    }
}
