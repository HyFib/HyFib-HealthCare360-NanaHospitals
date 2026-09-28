using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>
/// This server's <see cref="IAiTextService"/>: Claude when AI is enabled and configured for the facility, otherwise
/// the <see cref="DisabledAiTextService"/> behaviour. Settings are read once per scope (the settings service caches
/// them), so switching AI off takes effect on the next request.
/// </summary>
internal sealed class AiTextServiceRouter(AiSettingsReader settings, ClaudeTextService claude) : IAiTextService, IAiGateway
{
    private AiTextSettings? _settings;

    /// <remarks>
    /// The platform contract is synchronous. The first read in a scope blocks on the (memory-cached) settings lookup;
    /// this module's own code uses <see cref="GetAvailabilityAsync"/> instead.
    /// </remarks>
    public bool IsEnabled => (_settings ??= settings.GetTextSettingsAsync(CancellationToken.None).GetAwaiter().GetResult()).IsUsable;

    public async Task<AiAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken);
        return new AiAvailability(current.IsUsable, current.Provider, current.IsUsable ? current.Model : null, current.Reason);
    }

    public async Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken);
        return current.IsUsable ? await claude.CompleteAsync(request, current, cancellationToken) : DisabledAiTextService.Refuse(current.Reason);
    }

    private async Task<AiTextSettings> LoadAsync(CancellationToken cancellationToken) =>
        _settings ??= await settings.GetTextSettingsAsync(cancellationToken);
}
