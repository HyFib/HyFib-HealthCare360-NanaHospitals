using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>
/// The endpoints the ABDM gateway calls back on the hospital (HIP), at the paths the gateway uses (as NHA's ABDM wrapper
/// serves them): the answers of HIP-initiated linking (<c>/api/v3/hip/token/on-generate-token</c>,
/// <c>/api/v3/link/on_carecontext</c>), and the three calls of user-initiated linking, which HC360 answers with calls of
/// its own (<c>/api/v3/hip/patient/care-context/discover</c>, <c>/api/v3/hip/link/care-context/init</c> and
/// <c>…/confirm</c>, <see cref="AbdmUserLinkingService"/>), and consent and data flow
/// (<c>/api/v3/consent/request/hip/notify</c>, <c>/api/v3/hip/health-information/request</c>,
/// <see cref="AbdmDataFlowService"/>). The answers name the call by its <c>REQUEST-ID</c> header. A
/// call for another HIP (<c>X-HIP-ID</c>) is logged and ignored. Anonymous at the
/// HTTP level and rate-limited; the gateway proves the call is its own with a bearer token signed by a key it publishes
/// (<see cref="AbdmGatewayKeys"/>), and anything else is refused with 401. While ABDM is switched off they answer 503, as
/// every ABDM endpoint does, and while the gateway is off or not set up, 404. A call the gateway makes is accepted (202) as the gateway expects, also when it
/// answers no request of this hospital's, which is logged and ignored.
/// </summary>
internal static class AbdmGatewayCallbackEndpoints
{
    public const string OnGenerateTokenPath = "/api/v3/hip/token/on-generate-token";
    public const string OnAddCareContextsPath = "/api/v3/link/on_carecontext";
    public const string DiscoverPath = "/api/v3/hip/patient/care-context/discover";
    public const string LinkInitPath = "/api/v3/hip/link/care-context/init";
    public const string LinkConfirmPath = "/api/v3/hip/link/care-context/confirm";
    public const string ConsentNotifyPath = "/api/v3/consent/request/hip/notify";
    public const string HealthInformationRequestPath = "/api/v3/hip/health-information/request";

    public const string RequestIdHeader = "REQUEST-ID";
    public const string HipIdHeader = "X-HIP-ID";

    /// <summary>A callback body is a few hundred bytes; a discovery's a few kilobytes.</summary>
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
            HandleAsync<AbdmGatewayMessages.OnGenerateToken>(request, configuration, keys, loggers, (answer, _, innerCt) => linking.OnLinkTokenAsync(answer, innerCt), ct));
        callbacks.MapPost(OnAddCareContextsPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmLinkingService linking, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.OnAddCareContexts>(request, configuration, keys, loggers, (answer, _, innerCt) => linking.OnCareContextsAsync(answer, innerCt), ct));

        callbacks.MapPost(DiscoverPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmUserLinkingService linking, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.Discover>(request, configuration, keys, loggers, (call, requestId, innerCt) => linking.DiscoverAsync(call, requestId ?? call.RequestId ?? string.Empty, innerCt), ct));
        callbacks.MapPost(LinkInitPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmUserLinkingService linking, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.LinkInit>(request, configuration, keys, loggers, (call, requestId, innerCt) => linking.InitAsync(call, requestId ?? string.Empty, innerCt), ct));
        callbacks.MapPost(LinkConfirmPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmUserLinkingService linking, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.LinkConfirm>(request, configuration, keys, loggers, (call, requestId, innerCt) => linking.ConfirmAsync(call, requestId ?? call.RequestId ?? string.Empty, innerCt), ct));

        callbacks.MapPost(ConsentNotifyPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmDataFlowService flow, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.ConsentNotify>(request, configuration, keys, loggers, (call, requestId, innerCt) => flow.NotifyAsync(call, requestId ?? call.RequestId ?? string.Empty, innerCt), ct));
        callbacks.MapPost(HealthInformationRequestPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmDataFlowService flow, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.HealthInformationRequest>(request, configuration, keys, loggers, (call, requestId, innerCt) => flow.RequestAsync(call, requestId ?? call.RequestId ?? string.Empty, innerCt), ct));
    }

    private static async Task<IResult> HandleAsync<T>(
        HttpRequest request,
        AbdmGatewayConfiguration configuration,
        AbdmGatewayKeys keys,
        ILoggerFactory loggers,
        Func<T, string?, CancellationToken, Task> handle,
        CancellationToken ct)
        where T : class
    {
        var (settings, _, problems) = await configuration.ReadAsync(ct);
        if (!settings.Enabled || problems.Count > 0)
        {
            return TypedResults.NotFound();
        }

        var log = loggers.CreateLogger(typeof(AbdmGatewayCallbackEndpoints));
        var verified = await keys.VerifyAsync(settings, request.Headers.Authorization.ToString(), ct);
        if (verified.IsFailure)
        {
            log.LogWarning("Refused a call to the ABDM gateway callback {Path}: {Reason}", request.Path.Value, verified.Error!.Message);
            return TypedResults.Unauthorized();
        }

        if (request.Headers[HipIdHeader].ToString() is { Length: > 0 } hipId && !string.Equals(hipId, settings.HipId, StringComparison.Ordinal))
        {
            log.LogWarning("Ignored a call to the ABDM gateway callback {Path} for another HIP", request.Path.Value);
            return TypedResults.Accepted((string?)null);
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

        var requestId = request.Headers[RequestIdHeader].ToString();
        await handle(answer, requestId.Length is > 0 and <= 64 ? requestId : null, ct);
        return TypedResults.Accepted((string?)null);
    }
}
