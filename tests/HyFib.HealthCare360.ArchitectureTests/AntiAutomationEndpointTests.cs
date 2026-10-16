using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// The portal's anonymous endpoints that a script could use to send SMS or fill a queue ask for a proof of work
/// (SRS SEC-06, SECURITY.md "Anti-automation"); removing it from one is a visible change here.
/// </summary>
public sealed class AntiAutomationEndpointTests
{
    [Theory]
    [InlineData("POST", "/api/v1/auth/otp/request", AntiAutomationActions.Otp)]
    [InlineData("POST", "/api/v1/auth/otp/verify", AntiAutomationActions.Otp)]
    [InlineData("POST", "/api/v1/public/feedback/forms/{token}", AntiAutomationActions.Feedback)]
    [InlineData("POST", "/api/v1/public/feedback/invitations/{token}", AntiAutomationActions.Feedback)]
    public void Abusable_anonymous_endpoints_ask_for_a_proof_of_work(string method, string route, string action) =>
        Endpoint(method, route).Metadata.GetMetadata<RequiresProofOfWorkMetadata>().ShouldNotBeNull().Action.ShouldBe(action);

    /// <summary>
    /// Endpoint filters run in the order they were added, which is the order of their metadata: a form filled in wrongly
    /// is answered by validation before its proof is checked, so it neither spends the proof nor counts as a failure.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/v1/public/feedback/forms/{token}")]
    [InlineData("POST", "/api/v1/public/feedback/invitations/{token}")]
    public void A_validated_request_is_validated_before_its_proof_is_checked(string method, string route)
    {
        var metadata = Endpoint(method, route).Metadata.ToList();
        var validated = metadata.FindIndex(m => m is ValidatedRequest);

        validated.ShouldBeGreaterThanOrEqualTo(0);
        validated.ShouldBeLessThan(metadata.FindIndex(m => m is RequiresProofOfWorkMetadata));
    }

    [Theory]
    [InlineData("POST", "/api/v1/auth/login")]
    [InlineData("POST", "/api/v1/auth/refresh")]
    [InlineData("POST", "/api/v1/public/challenge")]
    [InlineData("POST", "/api/v1/public/unsubscribe/{token}")]
    public void Others_do_not(string method, string route) =>
        Endpoint(method, route).Metadata.GetMetadata<RequiresProofOfWorkMetadata>().ShouldBeNull();

    private static RouteEndpoint Endpoint(string method, string route) =>
        ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/') == route
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
}
