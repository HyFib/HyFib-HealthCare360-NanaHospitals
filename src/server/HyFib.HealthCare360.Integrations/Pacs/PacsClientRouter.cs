namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// The platform's <see cref="IPacsClient"/>. It is always registered but inactive until an administrator configures
/// PACS. The contract has no failure channel, so a study search that cannot run (PACS not configured, disabled,
/// unreachable or answering with an error) throws <see cref="HttpRequestException"/> with a staff-facing message: the
/// Diagnostics module reports that as "PACS could not be reached" rather than "no images yet", and it never surfaces as
/// a server error.
/// </summary>
internal sealed class PacsClientRouter(IEnumerable<IPacsProvider> providers, IntegrationSettingsReader settings) : IPacsClient
{
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        var (provider, config, _) = await ResolveAsync(cancellationToken);
        return provider is not null && await provider.PingAsync(config!.Settings!, config.Secrets, cancellationToken);
    }

    /// <exception cref="HttpRequestException">PACS is not usable; the message is safe to show to staff.</exception>
    public async Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, CancellationToken cancellationToken)
    {
        var (provider, config, problem) = await ResolveAsync(cancellationToken);
        if (provider is null)
        {
            throw new HttpRequestException(problem ?? "The PACS is not available.");
        }

        try
        {
            return await provider.FindStudiesAsync(query, config!.Settings!, config.Secrets, cancellationToken);
        }
        catch (IntegrationException ex)
        {
            // The provider has already written the integration log entry; callers only need the transport-style failure.
            throw new HttpRequestException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Viewer launch URL from <c>viewerUrlTemplate</c> (null when none is configured). Synchronous by contract; the
    /// settings read is served from the settings cache.
    /// </summary>
    public string? BuildViewerUrl(string studyInstanceUid, string? accessionNumber)
    {
        var config = settings.Read<PacsSettings, PacsSecrets>(IntegrationNames.Pacs, requireEnabled: false);
        return QidoQuery.ViewerUrl(config.Settings?.ViewerUrlTemplate, studyInstanceUid, accessionNumber);
    }

    private async Task<(IPacsProvider? Provider, IntegrationConfig<PacsSettings, PacsSecrets>? Config, string? Problem)> ResolveAsync(CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<PacsSettings, PacsSecrets>(IntegrationNames.Pacs, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            return (null, null, config.Problem);
        }

        var provider = providers.FirstOrDefault(p => p.Key == key);
        return provider is null ? (null, null, $"Unknown PACS provider '{key}'. Use dicomweb or simulator.") : (provider, config, null);
    }
}
