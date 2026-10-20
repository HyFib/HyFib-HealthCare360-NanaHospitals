namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>The provider <c>ai.provider</c> chose (null when it names none this server has) and its settings.</summary>
internal sealed record AiTextSelection(IAiTextProvider? Provider, AiTextSettings Settings);

/// <summary>
/// The AI text providers this server has, by their <c>ai.provider</c> key: <c>anthropic</c>, also when the setting is
/// unset. The setting is read per facility, falling back to the one for all facilities. A key no provider has turns AI
/// text off instead of falling back to another provider, which would send text to a service the hospital did not
/// choose. Only <see cref="AiTextServiceRouter"/> uses the registry, so every provider is reached through the router's
/// guards.
/// </summary>
internal sealed class AiTextProviderRegistry
{
    private readonly Dictionary<string, IAiTextProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="InvalidOperationException">Two providers have the same key.</exception>
    public AiTextProviderRegistry(IEnumerable<IAiTextProvider> providers)
    {
        foreach (var provider in providers)
        {
            if (!_providers.TryAdd(provider.Key, provider))
            {
                throw new InvalidOperationException($"Two AI text providers use the key '{provider.Key}'.");
            }
        }
    }

    public IReadOnlyCollection<string> Keys => _providers.Keys;

    public async Task<AiTextSelection> SelectAsync(AiSettingsReader settings, CancellationToken cancellationToken)
    {
        var (enabled, key) = await settings.GetTextChoiceAsync(cancellationToken);
        return _providers.TryGetValue(key, out var provider)
            ? new AiTextSelection(provider, await provider.ReadSettingsAsync(settings, enabled, cancellationToken))
            : new AiTextSelection(null, AiTextSettings.NotAvailable(enabled, key));
    }
}
