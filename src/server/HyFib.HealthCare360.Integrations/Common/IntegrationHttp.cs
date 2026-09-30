using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.Extensions.Http.Resilience;

namespace HyFib.HealthCare360.Integrations.Common;

/// <summary>HTTP plumbing shared by the adapters: typed-client registration, JSON bodies, endpoint validation.</summary>
internal static class IntegrationHttp
{
    /// <summary>Provider answers are kilobytes; anything larger is refused rather than buffered.</summary>
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Wire JSON for provider requests: property names exactly as written (providers use snake_case/PascalCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Registers a typed client behind the standard resilience pipeline (rate limiter, total/attempt timeouts, retry,
    /// circuit breaker). Unsafe methods are never retried at the HTTP layer: the provider may already have sent the SMS
    /// or created the order when a response is lost. The outbox retries whole messages instead, with the message id
    /// as provider reference (ADR-008).
    /// </summary>
    public static void AddIntegrationHttpClient<TClient>(this IServiceCollection services, TimeSpan attemptTimeout, TimeSpan totalTimeout)
        where TClient : class
    {
        services.AddHttpClient<TClient>(client =>
            {
                // Backstop only; the resilience pipeline times out first.
                client.Timeout = totalTimeout + TimeSpan.FromSeconds(5);
                client.MaxResponseContentBufferSize = MaxResponseBytes;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("HyFib-HealthCare360/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(OutboundHttp.CreateHandler)
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = attemptTimeout;
                options.TotalRequestTimeout.Timeout = totalTimeout;
                // Options validation requires the breaker's sampling window to be at least twice the attempt timeout.
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, attemptTimeout.Ticks * 2));
                options.Retry.DisableForUnsafeHttpMethods();
            });
    }

    public static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

    /// <summary>
    /// Validates an administrator-configured endpoint: absolute http(s) URL without embedded credentials; https is
    /// required for internet providers so credentials and PHI never cross the internet in clear text.
    /// </summary>
    public static bool TryCreateEndpoint(string? value, bool requireHttps, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var schemeOk = parsed.Scheme == Uri.UriSchemeHttps || (!requireHttps && parsed.Scheme == Uri.UriSchemeHttp);
        if (!schemeOk || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Appends a relative path to a base URL keeping the base path (<c>new Uri(base, rel)</c> would drop it).</summary>
    public static Uri Combine(Uri baseUri, string relative) =>
        new(baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + relative.TrimStart('/'));

    public static void SetBasicAuth(HttpRequestMessage request, string user, string password) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    public static bool TryParseJson(string body, [NotNullWhen(true)] out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    /// <summary>Reads a nested value (<c>data.id</c>, <c>messages.0.id</c>) as text; numbers and booleans are returned as written.</summary>
    public static string? ReadPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
            {
                current = child;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                     && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return Text(current);
    }

    public static string? Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static string? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? Text(value) : null;

    /// <summary>Response text for the integration log (bounded; the platform logger masks phones/e-mails/secrets).</summary>
    public static string Summarize(HttpResponseMessage response, string? body) =>
        $"HTTP {(int)response.StatusCode} {IntegrationTrace.Truncate(body, 500)}";
}
