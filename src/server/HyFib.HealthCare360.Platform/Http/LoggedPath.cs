using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>Endpoint metadata: the request path carries a secret, such as a patient's link token or a report verification code.</summary>
public sealed class SensitivePathMetadata
{
    public static readonly SensitivePathMetadata Instance = new();

    private SensitivePathMetadata()
    {
    }
}

/// <summary>
/// The request path as it may be written to request logs, error logs, traces and stored records. Patient links carry
/// their secret in the path (feedback and unsubscribe tokens, verification codes), and anyone who can read the logs
/// must not be able to use them. On endpoints marked with <see cref="WithSensitivePath{TBuilder}"/>, the route template
/// is recorded instead, for example <c>/api/v1/public/unsubscribe/{token}</c>.
/// </summary>
public static class LoggedPath
{
    /// <summary>Records the route template instead of the path in logs and traces.</summary>
    public static TBuilder WithSensitivePath<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(SensitivePathMetadata.Instance);

    public static string Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        // The exception handler clears the endpoint before it runs; the original one stays on its feature.
        var endpoint = http.GetEndpoint() ?? http.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        if (endpoint?.Metadata.GetMetadata<SensitivePathMetadata>() is not null)
        {
            return endpoint is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } template }
                ? "/" + template.TrimStart('/')
                : "(path withheld)";
        }

        return http.Request.Path.Value is { Length: > 0 } path ? path : "/";
    }
}
