using HyFib.HealthCare360.Modules.Interop;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// ABDM health records (SRS EHR-08): every <c>/api/v1/abdm</c> endpoint stops with the ABDM switch, which is off until an
/// administrator turns it on; exporting a record and linking records to a patient's ABHA need <c>abdm.export</c>, and
/// the gateway's set-up is for those who manage integrations. The scan-and-share queue is the registration desk's
/// (<c>patients.register</c>, and the contact permission for a share's full details); checking a patient's ABHA with
/// ABDM needs the identity permission, as an Aadhaar check does. The gateway's callbacks (the answers
/// of HIP-initiated linking, the three calls of user-initiated linking, consent notifications, requests for health
/// information and profile shares) are anonymous (it signs them instead) and stop with the switch too.
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
    public void The_abdm_api_is_six_document_exports_the_gateway_status_link_requests_the_share_queue_and_abha_checks()
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
            "GET /api/v1/abdm/patients/{patientId:guid}/abha-checks",
            "GET /api/v1/abdm/shares/",
            "GET /api/v1/abdm/shares/{id:guid}",
            "POST /api/v1/abdm/abha-checks/{id:guid}/code",
            "POST /api/v1/abdm/links/",
            "POST /api/v1/abdm/patients/{patientId:guid}/abha-checks",
            "POST /api/v1/abdm/shares/{id:guid}/dismiss",
            "POST /api/v1/abdm/shares/{id:guid}/registered",
        ]);
    }

    [Fact]
    public void Every_abdm_endpoint_stops_with_the_abdm_switch() =>
        Endpoints.Value.Concat(Callbacks.Value)
            .Where(e => !e.Metadata.GetOrderedMetadata<RequiresFeatureMetadata>().Any(r => r.Feature == FeatureKeys.Abdm && r.AppliesTo is null))
            .Select(Route).ShouldBeEmpty("Add .RequireFeature(FeatureKeys.Abdm) to these ABDM endpoints");

    [Fact]
    public void The_gateway_calls_back_on_eight_anonymous_posts_it_signs_and_nothing_else_is_under_api_v3()
    {
        Callbacks.Value.Select(e => $"{Method(e)} {Route(e)}").Order().ShouldBe(
        [
            "POST /api/v3/consent/request/hip/notify",
            "POST /api/v3/hip/health-information/request",
            "POST /api/v3/hip/link/care-context/confirm",
            "POST /api/v3/hip/link/care-context/init",
            "POST /api/v3/hip/patient/care-context/discover",
            "POST /api/v3/hip/patient/share",
            "POST /api/v3/hip/token/on-generate-token",
            "POST /api/v3/link/on_carecontext",
        ]);
        Callbacks.Value.ShouldAllBe(e => e.Metadata.GetMetadata<IAllowAnonymous>() != null);
    }

    [Fact]
    public void Exports_and_links_need_abdm_export_the_share_queue_registration_and_the_gateway_status_integrations_manage()
    {
        foreach (var endpoint in Endpoints.Value)
        {
            var route = Route(endpoint);
            var expected = route.StartsWith("/api/v1/abdm/bundles/", StringComparison.Ordinal)
                || route.StartsWith("/api/v1/abdm/links/", StringComparison.Ordinal)
                ? InteropPermissions.AbdmExport
                : route.StartsWith("/api/v1/abdm/shares/", StringComparison.Ordinal)
                ? PatientsPermissions.Register
                : route.EndsWith("/abha-checks", StringComparison.Ordinal) && Method(endpoint) == "GET"
                ? PatientsPermissions.Read
                : route.Contains("/abha-checks", StringComparison.Ordinal)
                ? PatientsPermissions.IdentityVerify
                : PlatformPermissions.IntegrationsManage;
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
                .ShouldContain(PermissionPolicyProvider.Prefix + expected, Route(endpoint));
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Route(endpoint));
        }

        // A share's full details are a patient's contact details, shown in full only with the contact permission.
        Endpoints.Value.Single(e => Route(e) == "/api/v1/abdm/shares/{id:guid}" && Method(e) == "GET")
            .Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
            .ShouldContain(PermissionPolicyProvider.Prefix + PatientsPermissions.ContactRead);
    }

    private static string Route(RouteEndpoint endpoint) => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    private static string Method(RouteEndpoint endpoint) => string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
}
