using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Integrations.Icd;

/// <summary>Which classification an <see cref="IIcdLookup"/> caller asked for.</summary>
internal enum IcdRevision
{
    Icd11 = 0,
    Icd10,

    /// <summary>A classification the WHO ICD-API does not serve (for example ICD-10-PCS procedures).</summary>
    Unsupported,
}

/// <summary>
/// The platform's <see cref="IIcdLookup"/>: online WHO ICD-API lookup with caching. It is always registered but stays
/// inactive until an administrator configures it: while online lookup is disabled or not configured it returns an
/// empty list, and the coder keeps working with the local catalogue (REL-03). When the ICD-API itself fails, it throws
/// <see cref="HttpRequestException"/> with a staff-facing message, which the Clinical module turns into its
/// "online service could not be reached" answer instead of showing an empty result as if nothing matched. With the
/// <c>simulator</c> provider it answers from <see cref="SimulatorIcdCatalogue"/> (training, no internet).
/// </summary>
internal sealed class IcdLookupRouter(
    IntegrationSettingsReader settings, WhoIcdApiClient who, IntegrationTrace trace, IMemoryCache cache, ILogger<IcdLookupRouter> logger) : IIcdLookup
{
    public async Task<IReadOnlyList<IcdSearchResult>> SearchAsync(string text, string release, int limit, CancellationToken cancellationToken)
    {
        var query = text?.Trim() ?? string.Empty;
        var revision = Revision(release);
        if (query.Length is < 2 or > 100 || revision == IcdRevision.Unsupported)
        {
            return [];
        }

        var config = await settings.ReadAsync<IcdSettings, IcdSecrets>(IntegrationNames.Icd, cancellationToken);
        if (config.ActiveProvider is not (ProviderKeys.WhoIcdApi or ProviderKeys.Simulator) || config.Settings is not { } icd)
        {
            return [];
        }

        if (revision == IcdRevision.Icd10 && !IcdResponseParser.IsIcd10Code(query))
        {
            // The ICD-API cannot search ICD-10 by text; that is the local catalogue's job.
            return [];
        }

        var take = Math.Clamp(limit, 1, 50);
        if (config.ActiveProvider == ProviderKeys.Simulator)
        {
            return await SimulateAsync(query, revision, take, icd);
        }

        var key = $"icd:{(revision == IcdRevision.Icd10 ? "10:" + icd.Icd10Release : "11:" + icd.Icd11Release)}:{icd.Language}:{take}:{query.ToUpperInvariant()}";
        if (cache.TryGetValue(key, out IReadOnlyList<IcdSearchResult>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var results = revision == IcdRevision.Icd10
                ? await who.LookupIcd10Async(query, take, icd, config.Secrets, cancellationToken)
                : await who.SearchIcd11Async(query, take, icd, config.Secrets, cancellationToken);
            cache.Set(key, results, TimeSpan.FromHours(Math.Clamp(icd.CacheHours, 1, 24 * 30)));
            return results;
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            // Failures are not cached, so the next search tries again. The message is safe to show to staff.
            var reason = IntegrationErrors.Describe(ex, "The ICD-API");
            logger.LogWarning("Online ICD lookup failed: {Reason}", reason);
            throw new HttpRequestException(reason, ex);
        }
    }

    private async Task<IReadOnlyList<IcdSearchResult>> SimulateAsync(string query, IcdRevision revision, int take, IcdSettings icd)
    {
        var started = trace.Start();
        var icd10 = revision == IcdRevision.Icd10;
        var results = icd10
            ? SimulatorIcdCatalogue.LookupIcd10(query, take, icd.Icd10Release)
            : SimulatorIcdCatalogue.SearchIcd11(query, take, icd.Icd11Release);
        await trace.LogAsync(IntegrationNames.Icd, ProviderKeys.Simulator, icd10 ? "icd10.lookup" : "icd11.search", true, started,
            reference: null, request: icd10 ? query.ToUpperInvariant() : "search", response: $"{results.Count} result(s) from the training list");
        return results;
    }

    /// <summary>
    /// Accepts the release labels callers use: <c>ICD-10 2019</c>, <c>ICD-10</c>, <c>icd10</c> or <c>10</c> for ICD-10;
    /// <c>ICD-11 MMS</c>, <c>11</c> or nothing for ICD-11 (the default). ICD-10-PCS is not served by the ICD-API.
    /// </summary>
    internal static IcdRevision Revision(string? release)
    {
        var normalized = new string((release ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (normalized.StartsWith("icd10pcs", StringComparison.Ordinal))
        {
            return IcdRevision.Unsupported;
        }

        return normalized == "10" || normalized.StartsWith("icd10", StringComparison.Ordinal) ? IcdRevision.Icd10 : IcdRevision.Icd11;
    }
}
