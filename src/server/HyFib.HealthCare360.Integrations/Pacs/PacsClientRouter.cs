namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// The platform's <see cref="IPacsClient"/>. It is always registered but inactive until an administrator configures
/// PACS. Searches and image retrievals that cannot run (PACS not configured, disabled, unreachable, answering with an
/// error, or sending something that is not a JPEG or PNG image within the size limit) throw
/// <see cref="HttpRequestException"/> with a staff-facing message: the Diagnostics module reports that as "PACS could
/// not be reached" rather than "no images yet", and it never surfaces as a server error. Every call reads the settings of
/// the facility it names (null: the signed-in facility), because background linking has no signed-in facility.
/// </summary>
internal sealed class PacsClientRouter(IEnumerable<IPacsProvider> providers, IntegrationSettingsReader settings) : IPacsClient
{
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        var (provider, config, _) = await ResolveAsync(null, cancellationToken);
        return provider is not null && await provider.PingAsync(config!.Settings!, config.Secrets, cancellationToken);
    }

    /// <exception cref="HttpRequestException">PACS is not usable; the message is safe to show to staff.</exception>
    public Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, CancellationToken cancellationToken) =>
        CallAsync(query.FacilityId, (provider, config) => provider.FindStudiesAsync(query, config.Settings!, config.Secrets, cancellationToken), cancellationToken);

    /// <exception cref="HttpRequestException">PACS is not usable; the message is safe to show to staff.</exception>
    public Task<IReadOnlyList<ImagingSeriesInfo>> FindSeriesAsync(string studyInstanceUid, Guid? facilityId, CancellationToken cancellationToken) =>
        CallAsync(facilityId, (provider, config) => provider.FindSeriesAsync(Valid(studyInstanceUid), config.Settings!, config.Secrets, cancellationToken),
            cancellationToken);

    /// <exception cref="HttpRequestException">PACS is not usable; the message is safe to show to staff.</exception>
    public Task<IReadOnlyList<ImagingInstanceInfo>> FindInstancesAsync(string studyInstanceUid, string seriesInstanceUid, Guid? facilityId, CancellationToken cancellationToken) =>
        CallAsync(facilityId, (provider, config) =>
            provider.FindInstancesAsync(Valid(studyInstanceUid), Valid(seriesInstanceUid), config.Settings!, config.Secrets, cancellationToken), cancellationToken);

    /// <exception cref="HttpRequestException">PACS is not usable or sent something HC360 does not pass on; the message is safe to show to staff.</exception>
    public Task<PacsImage> RetrieveImageAsync(PacsImageRequest request, CancellationToken cancellationToken) =>
        CallAsync(request.FacilityId, (provider, config) =>
        {
            Valid(request.StudyInstanceUid);
            Valid(request.SeriesInstanceUid);
            if (request.SopInstanceUid is { } instance)
            {
                Valid(instance);
            }

            return provider.RetrieveImageAsync(request, config.Settings!, config.Secrets, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Viewer launch URL from <c>viewerUrlTemplate</c> (null when none is configured, or when the template needs a value
    /// that is not given). Synchronous by contract; the settings read is served from the settings cache.
    /// </summary>
    public string? BuildViewerUrl(string? studyInstanceUid, string? accessionNumber)
    {
        var config = settings.Read<PacsSettings, PacsSecrets>(IntegrationNames.Pacs, null, requireEnabled: false);
        return QidoQuery.ViewerUrl(config.Settings?.ViewerUrlTemplate, studyInstanceUid, accessionNumber);
    }

    /// <summary>UIDs go into URL paths: anything but a DICOM UID is refused before a provider sees it.</summary>
    private static string Valid(string uid) =>
        QidoQuery.IsValidUid(uid) ? uid : throw new HttpRequestException("HC360 refused a DICOM UID that is not valid.");

    private async Task<T> CallAsync<T>(Guid? facilityId, Func<IPacsProvider, IntegrationConfig<PacsSettings, PacsSecrets>, Task<T>> call, CancellationToken cancellationToken)
    {
        var (provider, config, problem) = await ResolveAsync(facilityId, cancellationToken);
        if (provider is null)
        {
            throw new HttpRequestException(problem ?? "The PACS is not available.");
        }

        try
        {
            return await call(provider, config!);
        }
        catch (IntegrationException ex)
        {
            // The provider has already written the integration log entry; callers only need the transport-style failure.
            throw new HttpRequestException(ex.Message, ex);
        }
    }

    private async Task<(IPacsProvider? Provider, IntegrationConfig<PacsSettings, PacsSecrets>? Config, string? Problem)> ResolveAsync(
        Guid? facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<PacsSettings, PacsSecrets>(IntegrationNames.Pacs, facilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            return (null, null, config.Problem);
        }

        var provider = providers.FirstOrDefault(p => p.Key == key);
        return provider is null ? (null, null, $"Unknown PACS provider '{key}'. Use dicomweb or simulator.") : (provider, config, null);
    }
}
