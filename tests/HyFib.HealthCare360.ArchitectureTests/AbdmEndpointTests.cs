using HyFib.HealthCare360.Modules.Interop;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// ABDM health records (SRS EHR-08): every <c>/api/v1/abdm</c> endpoint stops with the ABDM switch, which is off until an
/// administrator turns it on; exporting a record needs <c>abdm.export</c>, and the gateway's set-up is for those who
/// manage integrations. The ABDM API reads only (GET).
/// </summary>
public sealed class AbdmEndpointTests
{
    private static readonly Lazy<IReadOnlyList<RouteEndpoint>> Endpoints = new(() =>
    [
        .. ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => ("/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/')).StartsWith("/api/v1/abdm/", StringComparison.Ordinal)),
    ]);

    [Fact]
    public void The_abdm_api_is_four_document_exports_and_the_gateway_status_all_read_only()
    {
        Endpoints.Value.Select(Route).Order().ShouldBe(
        [
            "/api/v1/abdm/bundles/discharge-summaries/{id:guid}",
            "/api/v1/abdm/bundles/lab-reports/{id:guid}",
            "/api/v1/abdm/bundles/op-consultations/{id:guid}",
            "/api/v1/abdm/bundles/prescriptions/{id:guid}",
            "/api/v1/abdm/gateway",
        ]);
        Endpoints.Value.ShouldAllBe(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.SequenceEqual(new[] { "GET" }));
    }

    [Fact]
    public void Every_abdm_endpoint_stops_with_the_abdm_switch() =>
        Endpoints.Value.Where(e => !e.Metadata.GetOrderedMetadata<RequiresFeatureMetadata>().Any(r => r.Feature == FeatureKeys.Abdm && r.AppliesTo is null))
            .Select(Route).ShouldBeEmpty("Add .RequireFeature(FeatureKeys.Abdm) to these ABDM endpoints");

    [Fact]
    public void Exports_need_abdm_export_and_the_gateway_status_needs_integrations_manage()
    {
        foreach (var endpoint in Endpoints.Value)
        {
            var expected = Route(endpoint).StartsWith("/api/v1/abdm/bundles/", StringComparison.Ordinal)
                ? InteropPermissions.AbdmExport
                : PlatformPermissions.IntegrationsManage;
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
                .ShouldContain(PermissionPolicyProvider.Prefix + expected, Route(endpoint));
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Route(endpoint));
        }
    }

    private static string Route(RouteEndpoint endpoint) => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
}
