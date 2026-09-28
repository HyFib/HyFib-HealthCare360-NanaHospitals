using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>AI switched off (the default, SEC-12): reports disabled and never sends anything anywhere.</summary>
internal sealed class DisabledAiTextService : IAiTextService
{
    public bool IsEnabled => false;

    public Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken) => Task.FromResult(Refuse(null));

    public static AiCompletion Refuse(string? reason) => new(false, null, null, 0, 0, reason ?? "AI features are turned off.");
}
