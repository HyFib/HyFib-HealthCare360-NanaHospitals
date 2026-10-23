using HyFib.HealthCare360.Modules.AI.Features.Assistant;
using HyFib.HealthCare360.Modules.AI.Features.Clinical;
using HyFib.HealthCare360.Modules.AI.Features.Status;
using HyFib.HealthCare360.Modules.AI.Features.Voice;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Assistant;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Modules.AI.Services.Clinical;
using HyFib.HealthCare360.Modules.AI.Services.OpenAi;
using HyFib.HealthCare360.Modules.AI.Services.Speech;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.AI;

public static class AIPermissions
{
    public const string AssistantUse = "ai.assistant.use";
    public const string DictationUse = "ai.dictation.use";
    public const string SettingsManage = "ai.settings.manage";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(AssistantUse, "AI assistant", "Use voice or typed commands (open patients, appointments, follow-ups, notes)",
            [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.FrontOffice, SystemRoles.HospitalAdmin]),
        new(DictationUse, "AI assistant", "Dictate clinical notes and get AI-structured SOAP drafts for review",
            [SystemRoles.Doctor, SystemRoles.HospitalAdmin]),
        new(SettingsManage, "AI assistant", "View AI provider status and usage", [SystemRoles.HospitalAdmin]),
    ];
}

/// <summary>
/// AI voice assistant (AIV-01..06, ADR-011, SEC-12): offline rule-based voice commands with an optional LLM fallback,
/// server speech-to-text, and clinical dictation structured into SOAP drafts, with the text provider the hospital
/// chooses (Anthropic, or an OpenAI-compatible server that can run inside the hospital). Off by default; every AI call
/// is redacted, rate-limited, recorded and audited, and nothing is saved without a clinician.
/// </summary>
public sealed class AIModule : IModule
{
    public string Name => "ai";

    public IEnumerable<PermissionDefinition> Permissions => AIPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AiSettingsReader>();

        // Text providers, chosen by ai.provider. They are reached only through the router (AiTextServiceRouter), which
        // applies the AI switch, redaction and usage recording to all of them.
        services.AddSingleton<IAnthropicClientProvider, AnthropicClientProvider>();
        services.AddScoped<IAiTextProvider, ClaudeTextService>();
        services.AddHttpClient<OpenAiCompatibleTextService>(client =>
            {
                // A backstop only: the provider's own time limit (at most 300 seconds) ends the call first.
                client.Timeout = TimeSpan.FromSeconds(350);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(OutboundHttp.CreateHandler)
            .AddHttpMessageHandler<CorrelationIdForwardingHandler>()
            .AddStandardResilienceHandler(options =>
            {
                // Structuring a long note on a model server in the hospital can take minutes. The POST is never retried:
                // the server may already be working on it, and a second run would take as long again.
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(320);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(320);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(640);
                options.Retry.DisableForUnsafeHttpMethods();
            });
        services.AddScoped<IAiTextProvider>(sp => sp.GetRequiredService<OpenAiCompatibleTextService>());
        services.AddScoped<AiTextProviderRegistry>();
        services.AddScoped<AiTextServiceRouter>();
        services.AddScoped<IAiTextService>(sp => sp.GetRequiredService<AiTextServiceRouter>());
        services.AddScoped<IAiGateway>(sp => sp.GetRequiredService<AiTextServiceRouter>());

        services.AddHttpClient<WhisperSpeechToText>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(200);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(OutboundHttp.CreateHandler)
            .AddHttpMessageHandler<CorrelationIdForwardingHandler>()
            .AddStandardResilienceHandler(options =>
            {
                // Transcribing 10 MB of audio on a CPU-only on-prem server can take minutes; the upload is never retried.
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(180);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(180);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(360);
                options.Retry.DisableForUnsafeHttpMethods();
            });
        services.AddScoped<SpeechToTextRouter>();
        services.AddScoped<ISpeechToText>(sp => sp.GetRequiredService<SpeechToTextRouter>());

        services.AddScoped<IAiUsageRecorder, AiUsageRecorder>();
        services.AddScoped<IAiQuota, AiQuota>();
        services.AddScoped<IPatientDirectory, PatientDirectory>();
        services.AddScoped<AssistantCommandService>();
        services.AddScoped<ClinicalNoteStructurer>();

        services.AddOptions<SlidingWindowLimit>(AiRateLimits.Policy)
            .Configure(AiRateLimits.Defaults)
            .Bind(configuration.GetSection(AiRateLimits.SectionName))
            .Validate(AiRateLimits.IsValid, $"{AiRateLimits.SectionName} needs a PermitLimit and SegmentsPerWindow of 1 or more and a Window longer than zero.")
            .ValidateOnStart();
        services.AddOptions<RateLimiterOptions>().Configure<IOptionsMonitor<SlidingWindowLimit>>((options, limits) =>
        {
            var limit = limits.Get(AiRateLimits.Policy);
            options.AddPolicy(AiRateLimits.Policy, http => AiRateLimits.Partition(http, limit));
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        AssistantEndpoints.Map(endpoints);
        ClinicalNoteEndpoints.Map(endpoints);
        VoiceEndpoints.Map(endpoints);
        AiStatusEndpoints.Map(endpoints);
    }
}
