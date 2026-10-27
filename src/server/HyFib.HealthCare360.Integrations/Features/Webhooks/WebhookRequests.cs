using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace HyFib.HealthCare360.Integrations.Features.Webhooks;

/// <summary>
/// Helpers for anonymous provider callbacks. Bodies are capped before they are read, the optional <c>facility</c>
/// query parameter selects whose credentials verify the call (webhooks carry no signed-in user), and allow-lists are
/// checked against the connection's address.
/// </summary>
internal static class WebhookRequests
{
    /// <summary>The platform's policy for partners' calls (1,200 requests a minute per client address): a campaign brings one receipt a message.</summary>
    public const string RateLimitPolicy = RateLimitPolicies.Partner;

    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>Raw body bytes (signatures are computed over the exact bytes), or null when larger than the cap.</summary>
    public static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        LimitBody(request);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Query-string plus form fields (form wins), for callbacks that post <c>application/x-www-form-urlencoded</c>.</summary>
    public static async Task<Dictionary<string, string>?> ReadFieldsAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        var fields = request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        if (request.HasFormContentType)
        {
            LimitBody(request);
            var form = await request.ReadFormAsync(cancellationToken);
            foreach (var (key, value) in form)
            {
                fields[key] = value.ToString();
            }
        }

        return fields;
    }

    public static Guid? Facility(HttpRequest request) =>
        Guid.TryParse(request.Query["facility"].ToString(), out var facility) ? facility : null;

    public static bool IsAllowed(HttpContext context, IReadOnlyList<string>? allowList)
    {
        if (allowList is not { Count: > 0 })
        {
            return true;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return false;
        }

        remote = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        return allowList.Any(entry => IPAddress.TryParse(entry.Trim(), out var allowed) && allowed.Equals(remote));
    }

    /// <summary>The URL as the provider called it (used when no public callback URL is configured).</summary>
    public static string RequestUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";

    private static void LimitBody(HttpRequest request)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }
    }
}
