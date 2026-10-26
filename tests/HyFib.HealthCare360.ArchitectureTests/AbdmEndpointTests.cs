using HyFib.HealthCare360.Modules.Interop;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// ABDM health records (SRS EHR-08): every <c>/api/v1/abdm</c> endpoint stops with the ABDM switch, which is off until an
/// administrator turns it on; exporting a record and linking records to a patient's ABHA need <c>abdm.export</c>, and
/// the gateway's set-up is for those who manage integrations. The only write is a link request. The gateway's callbacks
/// (the answers of HIP-initiated linking, and the three calls of user-initiated linking) are anonymous (it signs them
/// instead) and stop with the switch too.
/// </summary>
public sealed class AbdmEndpointTests
{
    private static readonly Lazy<IReadOnlyList<RouteEndpoint>> All = new(() =>
    [
        .. ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(),
    ]);

    private static readonly Lazy<IReadOnlyList<RouteEndpoint>> Endpoints = new(() =>
        [.. All.Value.Where(e => Route(e).StartsWith("/api/v1/abdm/", StringComparison.Ordinal))]);

    private static readonly Lazy<IReadOnlyList<RouteEndpoint>> Callbacks = new(() =>
        [.. All.Value.Where(e => Route(e).StartsWith("/api/v3/", StringComparison.Ordinal))]);

    [Fact]
    public void The_abdm_api_is_six_document_exports_the_gateway_status_and_link_requests()
    {
        Endpoints.Value.Select(e => $"{Method(e)} {Route(e)}").Order().ShouldBe(
        [
            "GET /api/v1/abdm/bundles/cardiology-reports/{id:guid}",
            "GET /api/v1/abdm/bundles/discharge-summaries/{id:guid}",
            "GET /api/v1/abdm/bundles/lab-reports/{id:guid}",
            "GET /api/v1/abdm/bundles/op-consultations/{id:guid}",
            "GET /api/v1/abdm/bundles/prescriptions/{id:guid}",
            "GET /api/v1/abdm/bundles/radiology-reports/{id:guid}",
            "GET /api/v1/abdm/gateway",
            "GET /api/v1/abdm/links/{id:guid}",
            "POST /api/v1/abdm/links/",
        ]);
    }

    [Fact]
    public void Every_abdm_endpoint_stops_with_the_abdm_switch() =>
        Endpoints.Value.Concat(Callbacks.Value)
            .Where(e => !e.Metadata.GetOrderedMetadata<RequiresFeatureMetadata>().Any(r => r.Feature == FeatureKeys.Abdm && r.AppliesTo is null))
            .Select(Route).ShouldBeEmpty("Add .RequireFeature(FeatureKeys.Abdm) to these ABDM endpoints");

    [Fact]
    public void The_gateway_calls_back_on_five_anonymous_posts_it_signs_and_nothing_else_is_under_api_v3()
    {
        Callbacks.Value.Select(e => $"{Method(e)} {Route(e)}").Order().ShouldBe(
        [
            "POST /api/v3/hip/link/care-context/confirm",
            "POST /api/v3/hip/link/care-context/init",
            "POST /api/v3/hip/patient/care-context/discover",
            "POST /api/v3/hip/token/on-generate-token",
            "POST /api/v3/link/on_carecontext",
        ]);
        Callbacks.Value.ShouldAllBe(e => e.Metadata.GetMetadata<IAllowAnonymous>() != null);
    }

    [Fact]
    public void Exports_and_links_need_abdm_export_and_the_gateway_status_needs_integrations_manage()
    {
        foreach (var endpoint in Endpoints.Value)
        {
            var expected = Route(endpoint).StartsWith("/api/v1/abdm/bundles/", StringComparison.Ordinal)
                || Route(endpoint).StartsWith("/api/v1/abdm/links/", StringComparison.Ordinal)
                ? InteropPermissions.AbdmExport
                : PlatformPermissions.IntegrationsManage;
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
                .ShouldContain(PermissionPolicyProvider.Prefix + expected, Route(endpoint));
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Route(endpoint));
        }
    }

    private static string Route(RouteEndpoint endpoint) => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    private static string Method(RouteEndpoint endpoint) => string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
}
