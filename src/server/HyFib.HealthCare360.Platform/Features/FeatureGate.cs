using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Features;

/// <summary>Endpoint metadata: the endpoint answers only while the feature is on (see <see cref="FeatureEndpointConventions"/>).</summary>
public sealed class RequiresFeatureMetadata
{
    public RequiresFeatureMetadata(string feature, Func<HttpRequest, bool>? appliesTo = null)
    {
        Feature = FeatureCatalog.Get(feature).Key;
        AppliesTo = appliesTo;
    }

    /// <summary>The feature's setting key.</summary>
    public string Feature { get; }

    /// <summary>The requests the switch applies to; null for every request.</summary>
    public Func<HttpRequest, bool>? AppliesTo { get; }
}

public static class FeatureEndpointConventions
{
    /// <summary>
    /// While the feature is off the endpoint answers 503 with the problem code <c>feature.disabled</c>, before its handler
    /// runs. The switch is read for the caller's facility, else hospital-wide. Works on minimal APIs, groups and hubs
    /// (their negotiation and connection).
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string feature)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresFeatureMetadata(feature));

    /// <summary>As <see cref="RequireFeature{TBuilder}(TBuilder, string)"/>, for the requests <paramref name="appliesTo"/> picks out.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string feature, Func<HttpRequest, bool> appliesTo)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresFeatureMetadata(feature, appliesTo));
}

/// <summary>The answers a switched-off feature gives, worded for the person who will read them.</summary>
public static class FeatureProblems
{
    public const string Code = "feature.disabled";

    /// <summary>503 <c>feature.disabled</c>: the whole endpoint is off. The apps show the detail instead of retrying.</summary>
    public static IResult Disabled(FeatureDefinition feature, bool forPatients) =>
        TypedResults.Problem(
            detail: Detail(feature, forPatients),
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: feature.OffTitle,
            extensions: new Dictionary<string, object?> { ["code"] = Code, ["data"] = new { feature = feature.Code } });

    /// <summary>
    /// 422 <c>feature.disabled</c>, for an option of a request that otherwise still works (a video appointment, when
    /// in-person booking goes on).
    /// </summary>
    public static Error DisabledError(FeatureDefinition feature, bool forPatients) =>
        new(Code, Detail(feature, forPatients)) { Data = new { feature = feature.Code } };

    /// <summary>Who reads the answer: patients and anonymous callers (the portal, SMS links), or staff.</summary>
    public static bool IsForPatients(ICurrentUser user) => !user.IsAuthenticated || user.IsPatient;

    private static string Detail(FeatureDefinition feature, bool forPatients) =>
        forPatients ? feature.OffDetailForPatients : feature.OffDetailForStaff;
}

/// <summary>
/// Answers 503 <c>feature.disabled</c> for an endpoint whose feature is off (<see cref="RequiresFeatureMetadata"/>).
/// Runs after authorization, so a caller who may not use the endpoint still gets 401/403, and before the idempotency
/// store, so nothing is kept for a refused write. Patients use HC360 only through the portal: while it is off, a
/// patient's sign-in opens no endpoint that needs one, whatever the endpoint says.
/// </summary>
internal sealed class FeatureGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IFeatureFlags features, ICurrentUser user)
    {
        if (context.GetEndpoint() is { } endpoint)
        {
            foreach (var feature in RequiredFeatures(context, endpoint, user))
            {
                if (!await features.IsEnabledAsync(feature, user.FacilityId, context.RequestAborted))
                {
                    await FeatureProblems.Disabled(FeatureCatalog.Get(feature), FeatureProblems.IsForPatients(user)).ExecuteAsync(context);
                    return;
                }
            }
        }

        await next(context);
    }

    internal static IEnumerable<string> RequiredFeatures(HttpContext context, Endpoint endpoint, ICurrentUser user)
    {
        var features = endpoint.Metadata.GetOrderedMetadata<RequiresFeatureMetadata>()
            .Where(requirement => requirement.AppliesTo?.Invoke(context.Request) ?? true)
            .Select(requirement => requirement.Feature);
        if (user.IsAuthenticated && user.IsPatient && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
        {
            features = features.Append(FeatureKeys.PatientPortal);
        }

        return features.Distinct(StringComparer.Ordinal);
    }
}
