using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>
/// The endpoints the ABDM gateway calls back on the hospital (HIP), at the paths the gateway uses (as NHA's ABDM wrapper
/// serves them): <c>/api/v3/hip/token/on-generate-token</c> and <c>/api/v3/link/on_carecontext</c>. Anonymous at the
/// HTTP level and rate-limited; the gateway proves the call is its own with a bearer token signed by a key it publishes
/// (<see cref="AbdmGatewayKeys"/>), and anything else is refused with 401. While ABDM is switched off they answer 503, as
/// every ABDM endpoint does, and while the gateway is off or not set up, 404. A call the gateway makes is accepted (202) as the gateway expects, also when it
/// answers no request of this hospital's, which is logged and ignored.
/// </summary>
internal static class AbdmGatewayCallbackEndpoints
{
    public const string OnGenerateTokenPath = "/api/v3/hip/token/on-generate-token";
    public const string OnAddCareContextsPath = "/api/v3/link/on_carecontext";

    /// <summary>A callback body is a few hundred bytes.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var callbacks = app.MapGroup(string.Empty)
            .WithTags("ABDM gateway callbacks")
            .RequireFeature(FeatureKeys.Abdm)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        callbacks.MapPost(OnGenerateTokenPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmLinkingService linking, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.OnGenerateToken>(request, configuration, keys, loggers, (answer, innerCt) => linking.OnLinkTokenAsync(answer, innerCt), ct));
        callbacks.MapPost(OnAddCareContextsPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmLinkingService linking, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.OnAddCareContexts>(request, configuration, keys, loggers, (answer, innerCt) => linking.OnCareContextsAsync(answer, innerCt), ct));
    }

    private static async Task<IResult> HandleAsync<T>(
        HttpRequest request,
        AbdmGatewayConfiguration configuration,
        AbdmGatewayKeys keys,
        ILoggerFactory loggers,
        Func<T, CancellationToken, Task> handle,
        CancellationToken ct)
        where T : class
    {
        var (settings, _, problems) = await configuration.ReadAsync(ct);
        if (!settings.Enabled || problems.Count > 0)
        {
            return TypedResults.NotFound();
        }

        var verified = await keys.VerifyAsync(settings, request.Headers.Authorization.ToString(), ct);
        if (verified.IsFailure)
        {
            loggers.CreateLogger(typeof(AbdmGatewayCallbackEndpoints)).LogWarning(
                "Refused a call to the ABDM gateway callback {Path}: {Reason}", request.Path.Value, verified.Error!.Message);
            return TypedResults.Unauthorized();
        }

        T? answer;
        try
        {
            answer = await JsonSerializer.DeserializeAsync<T>(request.Body, AbdmGatewayMessages.Json, ct);
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is null)
        {
            return Error.Validation("abdm.callback_body", "The gateway's callback body is not valid JSON of the expected shape.").ToProblem();
        }

        await handle(answer, ct);
        return TypedResults.Accepted((string?)null);
    }
}
