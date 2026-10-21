using System.Net;
using System.Reflection;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using HyFib.HealthCare360.Modules.AI.Features.Clinical;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Modules.AI.Services.Clinical;
using HyFib.HealthCare360.Modules.AI.Services.OpenAi;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Type = System.Type;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>
/// The guards every AI text provider is subject to (SEC-12, AIV-01..06), proved for each provider the server registers.
/// They sit in the router, the only way to a provider, which the first tests prove from the module's types. The others
/// drive each real provider through the real gateway and feature to a fake transport (the Anthropic SDK client, the
/// OpenAI-compatible server) and look at what arrived there: AI off stops the provider, it is handed only redacted text,
/// every call it receives is recorded, and whatever it answers only becomes a draft for a clinician to review.
/// </summary>
public sealed class AiGuardTests
{
    private const string Dictation =
        "Ravi Sharma, UHID NH26000123, mobile 9876543210, e-mail ravi.sharma@example.com. Fever for three days. Impression viral fever.";

    private const string Answer = """{"subjective":"Fever x 3 days.","objective":"","assessment":"Viral fever.","plan":"","suggestedIcd":[],"warnings":[]}""";

    private const string Off = "AI features are turned off for this facility (setting ai.enabled).";

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every provider the server registers (checked against the module's registrations below).</summary>
    private static readonly string[] ProviderKeys = [AiProviders.Anthropic, AiProviders.OpenAi];

    private static readonly Guid PatientId = Guid.Parse("0199a1b2-0000-7000-8000-000000000042");

    private readonly AiHarness _ai = new();
    private readonly IAnthropicClientProvider _anthropicClients = Substitute.For<IAnthropicClientProvider>();
    private readonly IAnthropicClient _anthropic = Substitute.For<IAnthropicClient>();
    private readonly StubHttpHandler _server = new();

    public AiGuardTests()
    {
        _anthropicClients.GetClient(default!, default).ReturnsForAnyArgs(_anthropic);
        _ai.Secret(AiSettingKeys.AnthropicSecret, "\"sk-ant-api03-test\"");
        _ai.Setting(AiSettingKeys.OpenAi, new OpenAiSettings { BaseUrl = "https://llm.hospital.lan/v1", Model = "local-model" });
        _ai.Secret(AiSettingKeys.OpenAiSecret, "\"server-key\"");
        _ai.Patients.RedactionContextAsync(PatientId, Arg.Any<CancellationToken>()).Returns(new RedactionContext(["Ravi Sharma"], ["NH26000123"]));
    }

    public static TheoryData<string> Providers() => new(ProviderKeys);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The router as the server builds it, with both real providers.</summary>
    private AiTextServiceRouter Router => _ai.Router(
        new ClaudeTextService(_anthropicClients, NullLogger<ClaudeTextService>.Instance),
        new OpenAiCompatibleTextService(new HttpClient(_server), NullLogger<OpenAiCompatibleTextService>.Instance));

    private Task<StructureOutcome> Structure(string specialty = "Internal medicine", CancellationToken? cancellationToken = null) =>
        new ClinicalNoteStructurer(Router, _ai.Clock).StructureAsync(new StructureNoteRequest(Dictation, specialty, PatientId), cancellationToken ?? Ct);

    private void Answers(string provider, string text)
    {
        if (provider == AiProviders.Anthropic)
        {
            _anthropic.Beta.Messages.Create(Arg.Any<MessageCreateParams>(), Arg.Any<CancellationToken>())
                .Returns(ClaudeResponseMapperTests.Message("end_turn", $$"""[{ "type": "text", "text": {{JsonSerializer.Serialize(text)}} }]"""));
        }
        else
        {
            _server.Respond(HttpStatusCode.OK, OpenAiCompatibleTextServiceTests.ChatCompletion(text));
        }
    }

    private void IsDown(string provider)
    {
        if (provider == AiProviders.Anthropic)
        {
            _anthropic.Beta.Messages.Create(Arg.Any<MessageCreateParams>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<BetaMessage>(new Anthropic5xxException(new HttpRequestException("529")) { StatusCode = (HttpStatusCode)529, ResponseBody = "{}" }));
        }
        else
        {
            _server.Respond(HttpStatusCode.ServiceUnavailable, "{}");
        }
    }

    private void Hangs(string provider)
    {
        if (provider == AiProviders.Anthropic)
        {
            _anthropic.Beta.Messages.Create(Arg.Any<MessageCreateParams>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ContinueWith<BetaMessage>(_ => throw new TaskCanceledException(), TaskScheduler.Default));
        }
        else
        {
            _server.Hang();
        }
    }

    /// <summary>The text of every call that reached the provider's transport, one entry per call.</summary>
    private IReadOnlyList<string> Transmitted(string provider) => provider == AiProviders.Anthropic
        ? [.. _anthropic.Beta.Messages.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<MessageCreateParams>().Select(Text)]
        : [.. _server.Requests.Select(r => r.Body ?? string.Empty)];

    private static string Text(MessageCreateParams parameters)
    {
        var parts = new List<string>();
        if (parameters.System is { } system && system.TryPickString(out var prompt))
        {
            parts.Add(prompt);
        }

        foreach (var message in parameters.Messages)
        {
            if (message.Content.TryPickString(out var content))
            {
                parts.Add(content);
            }
        }

        return string.Join("\n", parts);
    }

    [Fact]
    public async Task The_guards_are_proved_for_every_provider_the_server_registers()
    {
        var services = new ServiceCollection();
        new AIModule().AddServices(services, new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddTransient<CorrelationIdForwardingHandler>();
        await using var root = services.BuildServiceProvider();
        await using var scope = root.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<AiTextProviderRegistry>().Keys.ShouldBe(ProviderKeys, ignoreOrder: true);
    }

    [Fact]
    public void Only_the_router_reaches_a_provider()
    {
        // A service gets a provider only by asking for it, in a constructor or a minimal-API handler's parameters: only the
        // registry takes the providers (AiTextSelection only carries its choice), and only the router takes the registry.
        var allowed = new Dictionary<Type, Type[]>
        {
            [typeof(IAiTextProvider)] = [typeof(AiTextProviderRegistry), typeof(AiTextSelection)],
            [typeof(AiTextProviderRegistry)] = [typeof(AiTextServiceRouter)],
        };
        var offenders = new List<string>();
        foreach (var type in typeof(AIModule).Assembly.GetTypes())
        {
            foreach (var member in type.GetConstructors(Declared).Cast<MethodBase>().Concat(type.GetMethods(Declared)))
            {
                offenders.AddRange(member.GetParameters()
                    .Where(p => Reaches(p.ParameterType) is { } reached && !allowed[reached].Contains(Outermost(type)))
                    .Select(p => $"{type.FullName}.{member.Name}({p.ParameterType.Name} {p.Name})"));
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void A_provider_can_only_be_handed_a_redacted_request()
    {
        typeof(IAiTextProvider).GetMethod(nameof(IAiTextProvider.CompleteAsync))!.GetParameters()[0].ParameterType.ShouldBe(typeof(RedactedAiRequest));
        typeof(IAiTextProvider).GetMethods().SelectMany(m => m.GetParameters()).ShouldNotContain(p => p.ParameterType == typeof(AiRequest));

        // The only way to make one runs the redactor over the system prompt and every message.
        var redacted = typeof(RedactedAiRequest);
        redacted.IsSealed.ShouldBeTrue();
        redacted.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).ShouldAllBe(c => c.IsPrivate);
        redacted.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.ReturnType == redacted).Select(m => m.Name)
            .ShouldBe([nameof(RedactedAiRequest.Redact)]);
        redacted.GetProperties().ShouldAllBe(p => !p.CanWrite);
    }

    [Fact]
    public void The_ai_module_cannot_save_or_sign_a_note()
    {
        // Notes are saved and signed in the Clinical module, which the AI module does not reference: whatever a provider
        // answers can only go back to the screen, as a draft.
        typeof(AIModule).Assembly.GetReferencedAssemblies().Select(a => a.Name).ShouldNotContain("HyFib.HealthCare360.Modules.Clinical");
    }

    [Theory]
    [InlineData(AiProviders.Anthropic, null)]
    [InlineData(AiProviders.Anthropic, false)]
    [InlineData(AiProviders.OpenAi, null)]
    [InlineData(AiProviders.OpenAi, false)]
    public async Task Ai_turned_off_stops_the_provider(string provider, bool? enabled)
    {
        _ai.Setting(AiSettingKeys.Provider, provider);
        _ai.Setting(AiSettingKeys.Enabled, enabled);
        var router = Router;

        var outcome = await Structure();
        var platform = await ((IAiTextService)router).CompleteAsync(AiPrompts.StructureNote(Dictation, null), Ct);

        outcome.Failure.ShouldBe(Off);
        platform.ShouldBe(new AiCompletion(false, null, null, 0, 0, Off));
        router.IsEnabled.ShouldBeFalse();
        var availability = await router.GetAvailabilityAsync(Ct);
        availability.Enabled.ShouldBeFalse();
        availability.Provider.ShouldBe(provider);
        availability.Model.ShouldBeNull();
        Transmitted(AiProviders.Anthropic).ShouldBeEmpty();
        Transmitted(AiProviders.OpenAi).ShouldBeEmpty();
        _ai.Recorded.ShouldBeEmpty();
        await _ai.Settings.DidNotReceive().GetAsync<JsonElement>(Arg.Is<string>(k => k.EndsWith(".secret", StringComparison.Ordinal)), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task The_provider_is_handed_only_redacted_text(string provider)
    {
        _ai.TurnOn(provider);
        Answers(provider, Answer);

        var draft = (await Structure("Cardiology, referred by 9123456789")).Draft.ShouldNotBeNull();

        var sent = Transmitted(provider).ShouldHaveSingleItem();
        foreach (var identifier in new[] { "Ravi", "Sharma", "NH26000123", "9876543210", "ravi.sharma@example.com", "9123456789" })
        {
            sent.ShouldNotContain(identifier);
        }

        sent.ShouldContain("[NAME_1]");
        sent.ShouldContain("[PHONE_1]");
        sent.ShouldContain("Fever for three days.", customMessage: "Clinical content is sent as dictated.");
        draft.IdentifiersRedacted.ShouldBe(5);
        Transmitted(provider == AiProviders.Anthropic ? AiProviders.OpenAi : AiProviders.Anthropic).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Calls_from_other_modules_pass_the_same_guards(string provider)
    {
        _ai.TurnOn(provider);
        Answers(provider, "Noted.");
        IAiTextService platform = Router;

        var completion = await platform.CompleteAsync(new AiRequest(
            "billing.summary", "Summarise for the billing desk. Patient phone 9876543210.",
            [new AiMessage("user", "UHID NH26000123, e-mail ravi.sharma@example.com, ABHA 91-1234-5678-9012.")]), Ct);

        completion.Success.ShouldBeTrue();
        var sent = Transmitted(provider).ShouldHaveSingleItem();
        foreach (var identifier in new[] { "9876543210", "NH26000123", "ravi.sharma@example.com", "91-1234-5678-9012" })
        {
            sent.ShouldNotContain(identifier);
        }

        var usage = _ai.Recorded.ShouldHaveSingleItem();
        usage.Purpose.ShouldBe("billing.summary");
        usage.Provider.ShouldBe(provider);
        usage.Success.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Every_call_the_provider_receives_is_recorded(string provider)
    {
        _ai.TurnOn(provider);

        Answers(provider, Answer);
        await Structure();
        IsDown(provider);
        await Structure();

        _ai.Recorded.Count.ShouldBe(2);
        _ai.Recorded.ShouldAllBe(u => u.Purpose == "clinical.structure-note" && u.Provider == provider && u.PatientId == PatientId && u.Model != null);
        _ai.Recorded[0].Success.ShouldBeTrue();
        _ai.Recorded[0].Error.ShouldBeNull();
        _ai.Recorded[1].Success.ShouldBeFalse();
        _ai.Recorded[1].Error.ShouldBe("The AI service is temporarily unavailable.");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_call_the_user_abandons_is_still_recorded(string provider)
    {
        _ai.TurnOn(provider);
        Hangs(provider);
        using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        abandoned.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => Structure(cancellationToken: abandoned.Token));

        var usage = _ai.Recorded.ShouldHaveSingleItem();
        usage.Provider.ShouldBe(provider);
        usage.PatientId.ShouldBe(PatientId);
        usage.Success.ShouldBeFalse();
        usage.Error.ShouldBe("The call ended before the AI service answered.");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Whatever_the_provider_answers_is_only_a_draft_for_review(string provider)
    {
        _ai.TurnOn(provider);
        Answers(provider, """
            {"status":"signed","requiresClinicianReview":false,"signedBy":"AI","subjective":"Fever.","objective":"","assessment":"Viral fever.",
             "plan":"","suggestedIcd":[],"warnings":[]}
            """);

        var draft = (await Structure()).Draft.ShouldNotBeNull();

        draft.Status.ShouldBe("draft");
        draft.RequiresClinicianReview.ShouldBeTrue();
        draft.Warnings[0].ShouldBe(ClinicalNoteStructurer.ReviewWarning);
    }

    /// <summary>
    /// Whether a parameter of this type hands over a provider (also in a list, a factory or by reference), or the
    /// registry. A logger's category only names the type.
    /// </summary>
    private static Type? Reaches(Type type) =>
        type.HasElementType ? Reaches(type.GetElementType()!)
        : typeof(IAiTextProvider).IsAssignableFrom(type) ? typeof(IAiTextProvider)
        : type == typeof(AiTextProviderRegistry) ? typeof(AiTextProviderRegistry)
        : type.IsGenericType && type.GetGenericTypeDefinition() != typeof(ILogger<>)
            ? type.GetGenericArguments().Select(Reaches).FirstOrDefault(reached => reached is not null)
        : null;

    /// <summary>The type a nested (compiler-generated) type belongs to.</summary>
    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }
}
