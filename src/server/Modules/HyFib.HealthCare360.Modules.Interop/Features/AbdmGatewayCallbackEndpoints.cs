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
/// <see cref="AbdmDataFlowService"/>), and scan and share (<c>/api/v3/hip/patient/share</c>,
/// <see cref="AbdmProfileShareService"/>), and the consent manager's answers to the hospital as HIU
/// (<c>/api/v3/hiu/consent/request/on-init</c>, <c>…/notify</c>, <c>/api/v3/hiu/consent/on-fetch</c>,
/// <see cref="AbdmConsentRequestService"/>, and <c>/api/v3/hiu/health-information/on-request</c>,
/// <see cref="AbdmHealthInformationService"/>, checked against <c>X-HIU-ID</c>). Other providers post the records
/// themselves to <see cref="DataPushPath"/>, without a gateway token (<see cref="ReceiveAsync"/>). The answers name the call by its <c>REQUEST-ID</c> header. A
/// call for another HIP (<c>X-HIP-ID</c>) is logged and ignored. Anonymous at the
/// HTTP level and rate-limited as a partner's calls (<see cref="RateLimitPolicies.Partner"/>); the gateway proves the call is its own with a bearer token signed by a key it publishes
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
    public const string ProfileSharePath = "/api/v3/hip/patient/share";
    public const string ConsentOnInitPath = "/api/v3/hiu/consent/request/on-init";
    public const string HiuNotifyPath = "/api/v3/hiu/consent/request/notify";
    public const string ConsentOnFetchPath = "/api/v3/hiu/consent/on-fetch";
    public const string HealthInformationOnRequestPath = "/api/v3/hiu/health-information/on-request";

    /// <summary>The hospital's data-push URL: where other providers post the records it asked for, encrypted for it.</summary>
    public const string DataPushPath = "/api/v3/hiu/health-information/transfer";

    public const string RequestIdHeader = "REQUEST-ID";
    public const string HipIdHeader = "X-HIP-ID";
    public const string HiuIdHeader = "X-HIU-ID";

    /// <summary>A callback body is a few hundred bytes; a discovery's a few kilobytes.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    /// <summary>A pushed page holds a few documents, with any PDFs inside them.</summary>
    public const long MaxPushBytes = 32 * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var callbacks = app.MapGroup(string.Empty)
            .WithTags("ABDM gateway callbacks")
            .RequireFeature(FeatureKeys.Abdm)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Partner)
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

        callbacks.MapPost(ProfileSharePath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmProfileShareService shares, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.ProfileShare>(request, configuration, keys, loggers, (call, requestId, innerCt) => shares.ShareAsync(call, requestId ?? string.Empty, innerCt), ct));

        // The hospital as HIU (ABDM M3): the consent manager's answers about the patient's consent.
        callbacks.MapPost(ConsentOnInitPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmConsentRequestService consents, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.ConsentOnInit>(request, configuration, keys, loggers, (answer, _, innerCt) => consents.OnInitAsync(answer, innerCt), ct, asHiu: true));
        callbacks.MapPost(HiuNotifyPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmConsentRequestService consents, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.HiuNotify>(request, configuration, keys, loggers, (call, requestId, innerCt) => consents.NotifyAsync(call, requestId ?? call.RequestId ?? string.Empty, innerCt), ct, asHiu: true));
        callbacks.MapPost(ConsentOnFetchPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmConsentRequestService consents, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.ConsentOnFetch>(request, configuration, keys, loggers, (answer, _, innerCt) => consents.OnFetchAsync(answer, innerCt), ct, asHiu: true));
        callbacks.MapPost(HealthInformationOnRequestPath, (HttpRequest request, AbdmGatewayConfiguration configuration, AbdmGatewayKeys keys, AbdmHealthInformationService records, ILoggerFactory loggers, CancellationToken ct) =>
            HandleAsync<AbdmGatewayMessages.HiuOnHealthInformationRequest>(request, configuration, keys, loggers, (answer, _, innerCt) => records.OnRequestAsync(answer, innerCt), ct, asHiu: true));

        app.MapPost(DataPushPath, ReceiveAsync)
            .WithTags("ABDM gateway callbacks")
            .RequireFeature(FeatureKeys.Abdm)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Partner)
            .WithMetadata(new RequestSizeLimitAttribute(MaxPushBytes));
    }

    /// <summary>
    /// A page of records from another provider. It carries no gateway token (ABDM's data push has none): what admits it
    /// is a transaction id the consent manager gave for a request of this hospital's, still awaiting pages, and content
    /// that decrypts with that request's own key. Anything else is refused and nothing is kept.
    /// </summary>
    private static async Task<IResult> ReceiveAsync(HttpRequest request, AbdmGatewayConfiguration configuration, AbdmHealthInformationService records, CancellationToken ct)
    {
        var (settings, _, problems) = await configuration.ReadAsync(ct);
        if (!settings.Enabled || problems.Count > 0)
        {
            return TypedResults.NotFound();
        }

        AbdmGatewayMessages.ReceivedPush? push;
        try
        {
            push = await JsonSerializer.DeserializeAsync<AbdmGatewayMessages.ReceivedPush>(request.Body, AbdmGatewayMessages.Json, ct);
        }
        catch (JsonException)
        {
            push = null;
        }

        if (push is null)
        {
            return Error.Validation("abdm.push_body", "The page is not valid JSON of the expected shape.").ToProblem();
        }

        var received = await records.ReceiveAsync(push, ct);
        return received.IsFailure ? received.Error!.ToProblem() : TypedResults.Accepted((string?)null);
    }

    private static async Task<IResult> HandleAsync<T>(
        HttpRequest request,
        AbdmGatewayConfiguration configuration,
        AbdmGatewayKeys keys,
        ILoggerFactory loggers,
        Func<T, string?, CancellationToken, Task> handle,
        CancellationToken ct,
        bool asHiu = false)
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

        var (idHeader, ownId) = asHiu ? (HiuIdHeader, settings.HiuIdentity) : (HipIdHeader, settings.HipId);
        if (request.Headers[idHeader].ToString() is { Length: > 0 } calledId && !string.Equals(calledId, ownId, StringComparison.Ordinal))
        {
            log.LogWarning("Ignored a call to the ABDM gateway callback {Path} for another HIP or HIU", request.Path.Value);
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
