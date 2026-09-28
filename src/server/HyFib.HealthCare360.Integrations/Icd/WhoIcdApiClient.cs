using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Icd;

/// <summary>
/// OAuth2 client-credentials tokens for the ICD-API, cached per credential until a minute before expiry. A semaphore
/// makes concurrent searches share one token request instead of stampeding the token endpoint.
/// </summary>
internal sealed class IcdTokenCache(TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new(StringComparer.Ordinal);

    public async Task<string> GetAsync(string key, Func<CancellationToken, Task<(string Token, TimeSpan Lifetime)>> fetch, CancellationToken cancellationToken)
    {
        if (TryGet(key) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGet(key) is { } fresh)
            {
                return fresh;
            }

            var (token, lifetime) = await fetch(cancellationToken);
            _tokens[key] = (token, time.GetUtcNow() + lifetime - TimeSpan.FromMinutes(1));
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private string? TryGet(string key) =>
        _tokens.TryGetValue(key, out var entry) && entry.ExpiresAt > time.GetUtcNow() ? entry.Token : null;
}

/// <summary>
/// WHO ICD-API v2: ICD-11 MMS search (<c>/icd/release/11/{release}/mms/search</c>) and ICD-10 category lookup
/// (<c>/icd/release/10/{release}/{code}</c> plus its children). The API has no ICD-10 free-text search, so free text in
/// ICD-10 is served by the Clinical module's seeded catalogue (IIcdLookup contract).
/// </summary>
internal sealed class WhoIcdApiClient(HttpClient http, IcdTokenCache tokens, IntegrationTrace trace)
{
    private const int MaxIcd10Children = 10;

    public async Task<IReadOnlyList<IcdSearchResult>> SearchIcd11Async(string text, int limit, IcdSettings settings, IcdSecrets secrets, CancellationToken cancellationToken)
    {
        var release = Uri.EscapeDataString(settings.Icd11Release.Trim());
        var body = await GetAsync($"icd/release/11/{release}/mms/search?q={Uri.EscapeDataString(text)}&useFlexisearch=true&flatResults=true&highlightingEnabled=false",
            settings, secrets, "icd11.search", cancellationToken);
        return IcdResponseParser.ParseIcd11Search(body, settings.Icd11Release, limit);
    }

    public async Task<IReadOnlyList<IcdSearchResult>> LookupIcd10Async(string code, int limit, IcdSettings settings, IcdSecrets secrets, CancellationToken cancellationToken)
    {
        var release = settings.Icd10Release.Trim();
        var label = $"ICD-10 {release}";
        var entity = IcdResponseParser.ParseIcd10Entity(await GetAsync(
            $"icd/release/10/{Uri.EscapeDataString(release)}/{Uri.EscapeDataString(code.Trim().ToUpperInvariant())}", settings, secrets, "icd10.lookup", cancellationToken));
        if (entity is null)
        {
            return [];
        }

        var results = new List<IcdSearchResult> { new(entity.Code, entity.Title, label, null, entity.ChildPaths.Count == 0) };
        foreach (var path in entity.ChildPaths.Take(Math.Min(limit - 1, MaxIcd10Children)))
        {
            if (IcdResponseParser.ParseIcd10Entity(await GetAsync(path, settings, secrets, "icd10.lookup", cancellationToken)) is { } child)
            {
                results.Add(new IcdSearchResult(child.Code, child.Title, label, null, child.ChildPaths.Count == 0));
            }
        }

        return results;
    }

    private async Task<string> GetAsync(string relative, IcdSettings settings, IcdSecrets secrets, string operation, CancellationToken cancellationToken)
    {
        if (!IntegrationHttp.TryCreateEndpoint(settings.ApiBaseUrl, requireHttps: true, out var baseUri))
        {
            throw new IntegrationException("The ICD-API base URL must be an https address.");
        }

        var token = await TokenAsync(settings, secrets, cancellationToken);
        var started = trace.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, IntegrationHttp.Combine(baseUri, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.ParseAdd(string.IsNullOrWhiteSpace(settings.Language) ? "en" : settings.Language.Trim());
        request.Headers.TryAddWithoutValidation("API-Version", "v2");

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var path = relative.Split('?')[0];
        await trace.LogAsync(IntegrationNames.Icd, ProviderKeys.WhoIcdApi, operation, response.IsSuccessStatusCode, started, path, path,
            string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode}"),
            response.IsSuccessStatusCode ? null : $"ICD-API returned HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
        {
            throw new IntegrationException($"ICD-API returned HTTP {(int)response.StatusCode}.");
        }

        return body;
    }

    private Task<string> TokenAsync(IcdSettings settings, IcdSecrets secrets, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secrets.ClientId) || string.IsNullOrWhiteSpace(secrets.ClientSecret))
        {
            throw new IntegrationException("The ICD-API client id and secret are not configured.");
        }

        if (!IntegrationHttp.TryCreateEndpoint(settings.TokenUrl, requireHttps: true, out var tokenUri))
        {
            throw new IntegrationException("The ICD-API token URL must be an https address.");
        }

        // The cache key identifies the credential without keeping the client id itself in memory structures.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenUri.AbsoluteUri + "|" + secrets.ClientId)));
        return tokens.GetAsync(key, ct => RequestTokenAsync(tokenUri, secrets.ClientId, secrets.ClientSecret, ct), cancellationToken);
    }

    private async Task<(string Token, TimeSpan Lifetime)> RequestTokenAsync(Uri tokenUri, string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(
            [
                new("client_id", clientId),
                new("client_secret", clientSecret),
                new("scope", "icdapi_access"),
                new("grant_type", "client_credentials"),
            ]),
        };
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? token = null;
        var lifetime = TimeSpan.FromMinutes(50);
        if (response.IsSuccessStatusCode && IntegrationHttp.TryParseJson(body, out var document))
        {
            using (document)
            {
                token = IntegrationHttp.Property(document.RootElement, "access_token");
                if (int.TryParse(IntegrationHttp.Property(document.RootElement, "expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 120)
                {
                    lifetime = TimeSpan.FromSeconds(seconds);
                }
            }
        }

        var ok = !string.IsNullOrEmpty(token);
        await trace.LogAsync(IntegrationNames.Icd, ProviderKeys.WhoIcdApi, "token", ok, started, null, "client_credentials",
            string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode}"), ok ? null : "The ICD-API rejected the client credentials.");
        return ok ? (token!, lifetime) : throw new IntegrationException("The ICD-API rejected the client credentials.");
    }
}
