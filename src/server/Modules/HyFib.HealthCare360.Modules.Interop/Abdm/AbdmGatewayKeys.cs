using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>The gateway's key set as last fetched, shared by every callback check.</summary>
internal sealed class AbdmGatewayKeyCache
{
    /// <summary>A key set is fetched again after this long, and at most this often when a token names a key not in it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    public static readonly TimeSpan MinimumRefetch = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private (string Url, string Json, DateTimeOffset FetchedAt)? _keys;

    public (string Json, DateTimeOffset FetchedAt)? Get(string url)
    {
        lock (_lock)
        {
            return _keys is { } keys && keys.Url == url ? (keys.Json, keys.FetchedAt) : null;
        }
    }

    public void Set(string url, string json, DateTimeOffset at)
    {
        lock (_lock)
        {
            _keys = (url, json, at);
        }
    }
}

/// <summary>
/// Whether a call to HC360's gateway callbacks is the gateway's (<see cref="AbdmCallbackVerifier"/>): its bearer token
/// checked against the gateway's published key set, fetched from <see cref="AbdmGatewaySettings.KeySetUrl"/> and kept
/// for <see cref="AbdmGatewayKeyCache.Lifetime"/>. A token naming a key not in the set has the set fetched again (the
/// gateway rotated its keys), at most once a minute, before it is refused.
/// </summary>
internal sealed class AbdmGatewayKeys(HttpClient http, AbdmGatewayKeyCache cache, TimeProvider time, ILogger<AbdmGatewayKeys> log)
{
    /// <summary>A key set is a few kilobytes.</summary>
    private const int MaxKeySetBytes = 256 * 1024;

    public async Task<Result> VerifyAsync(AbdmGatewaySettings settings, string? authorization, CancellationToken ct)
    {
        if (settings.KeySetUrl is not { } url)
        {
            return Error.Rule(AbdmCallbackVerifier.UnauthenticatedCode, "The gateway's key set is not configured.");
        }

        var now = time.GetUtcNow();
        var cached = cache.Get(url);
        var keys = cached is { } fresh && now - fresh.FetchedAt < AbdmGatewayKeyCache.Lifetime ? fresh : await FetchAsync(url, now, ct);
        if (keys is null)
        {
            return Error.Rule(AbdmCallbackVerifier.UnauthenticatedCode, "The gateway's key set could not be fetched to check the call.");
        }

        var verified = await AbdmCallbackVerifier.VerifyAsync(authorization, keys.Value.Json, settings.CallbackIssuer, null, now, settings.CallbackClientId);
        if (verified.Error?.Code == AbdmCallbackVerifier.UnknownKeyCode && now - keys.Value.FetchedAt >= AbdmGatewayKeyCache.MinimumRefetch
            && await FetchAsync(url, now, ct) is { } refetched)
        {
            verified = await AbdmCallbackVerifier.VerifyAsync(authorization, refetched.Json, settings.CallbackIssuer, null, now, settings.CallbackClientId);
        }

        return verified.IsSuccess ? Result.Success() : verified.Error!;
    }

    private async Task<(string Json, DateTimeOffset FetchedAt)?> FetchAsync(string url, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxKeySetBytes)
            {
                log.LogWarning("The ABDM gateway's key set could not be fetched: HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (json.Length > MaxKeySetBytes)
            {
                return null;
            }

            cache.Set(url, json, now);
            return (json, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "The ABDM gateway's key set could not be fetched");
            return null;
        }
    }
}
