using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// The feature switches cover what the Features screen says they do: every patient-portal API, the portal's sign-in and
/// the public booking directory stop with the patient portal; every video consultation endpoint stops with video
/// consultations, and the telehealth hub checks them for the consultation's facility whenever someone joins; every ABDM
/// health record download stops with ABDM health records. A new portal, telehealth or ABDM endpoint gets its switch, or
/// these tests fail. What must keep working whatever is switched off (QR checks, provider callbacks, SMS links, staff
/// screens, discharge summary PDFs, the FHIR API) has no switch.
/// </summary>
public sealed class FeatureSwitchTests
{
    /// <summary>Video consultation endpoints that stay available while video is off, to close what is already open.</summary>
    private static readonly string[] OpenWhileVideoIsOff =
    [
        "POST /api/v1/telehealth/sessions/{id:guid}/end",
        "POST /api/v1/telehealth/sessions/{id:guid}/cancel",
    ];

    private static readonly Lazy<IReadOnlyList<SwitchedEndpoint>> Endpoints = new(() =>
    [
        .. ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(SwitchedEndpoint.Of),
    ]);

    [Fact]
    public void Every_portal_api_and_the_booking_directory_stop_with_the_portal()
    {
        var portal = Endpoints.Value.Where(e => e.Route.StartsWith("/api/v1/portal/", StringComparison.Ordinal)
                                                || e.Route.StartsWith("/api/v1/public/doctors", StringComparison.Ordinal)).ToList();

        portal.Count.ShouldBeGreaterThan(40, "the portal's own API and the public doctor directory");
        portal.Where(e => !e.AlwaysNeeds(FeatureKeys.PatientPortal)).Select(e => e.Key).Order()
            .ShouldBeEmpty("Add .RequireFeature(FeatureKeys.PatientPortal) to these portal endpoints");
    }

    [Theory]
    [InlineData("POST /api/v1/auth/otp/request")]
    [InlineData("POST /api/v1/auth/otp/verify")]
    public void Signing_in_and_registering_on_the_portal_stop_with_it(string key) =>
        Endpoints.Value.Single(e => e.Key == key).AlwaysNeeds(FeatureKeys.PatientPortal).ShouldBeTrue();

    [Fact]
    public void Only_the_portals_own_refreshes_stop_with_it()
    {
        var refresh = Endpoints.Value.Single(e => e.Key == "POST /api/v1/auth/refresh").Requirements
            .ShouldHaveSingleItem();

        refresh.Feature.ShouldBe(FeatureKeys.PatientPortal);
        var appliesTo = refresh.AppliesTo.ShouldNotBeNull();
        appliesTo(RequestTo(Listener.Portal, "portal")).ShouldBeTrue();
        appliesTo(RequestTo(Listener.Staff, "web")).ShouldBeFalse();
        appliesTo(RequestTo(Listener.Staff, "desktop")).ShouldBeFalse();

        // The portal is told by the address a request came to, not by what the request says it is.
        appliesTo(RequestTo(Listener.Portal, "web")).ShouldBeTrue();
        appliesTo(RequestTo(Listener.Staff, "portal")).ShouldBeFalse();
    }

    [Fact]
    public void Every_video_consultation_endpoint_stops_with_video_consultations()
    {
        var video = Endpoints.Value.Where(e => e.Route.StartsWith("/api/v1/telehealth/", StringComparison.Ordinal)
                                               || e.Route.StartsWith("/api/v1/portal/telehealth/", StringComparison.Ordinal)).ToList();

        video.Count.ShouldBeGreaterThan(10);
        video.Where(e => !OpenWhileVideoIsOff.Contains(e.Key) && !e.AlwaysNeeds(FeatureKeys.Telehealth)).Select(e => e.Key).Order()
            .ShouldBeEmpty("Add .RequireFeature(FeatureKeys.Telehealth) to these video consultation endpoints");
        video.Where(e => OpenWhileVideoIsOff.Contains(e.Key)).Select(e => e.Key).Order().ShouldBe(OpenWhileVideoIsOff.Order());
        video.Where(e => OpenWhileVideoIsOff.Contains(e.Key)).ShouldAllBe(e => e.Requirements.Count == 0);
    }

    [Fact]
    public void Every_abdm_endpoint_stops_with_abdm_health_records()
    {
        var abdm = Endpoints.Value.Where(e => e.Route.StartsWith("/api/v1/abdm/", StringComparison.Ordinal)).ToList();

        abdm.ShouldNotBeEmpty("the ABDM health record downloads");
        abdm.Where(e => !e.AlwaysNeeds(FeatureKeys.Abdm)).Select(e => e.Key).Order()
            .ShouldBeEmpty("Add .RequireFeature(FeatureKeys.Abdm) to these ABDM endpoints");
    }

    [Fact]
    public void The_hub_has_no_switch_because_every_join_checks_the_consultations_facility()
    {
        // Guests and portal patients connect without a facility, so a switch on the hub could only read the hospital-wide
        // one. TelehealthHub's join methods check the consultation's facility instead; nothing else works without joining.
        var hub = Endpoints.Value.Where(e => e.Route.StartsWith("/hubs/telehealth", StringComparison.Ordinal)).ToList();

        hub.ShouldNotBeEmpty();
        hub.ShouldAllBe(e => e.Requirements.Count == 0);
    }

    [Theory]
    [InlineData("GET /api/v1/lab/reports/verify/{code}")]
    [InlineData("GET /api/v1/radiology/reports/verify/{code}")]
    [InlineData("GET /api/v1/cardiology/reports/verify/{code}")]
    [InlineData("GET /api/v1/public/prescriptions/verify/{token}")]
    [InlineData("GET /api/v1/public/prescriptions/verify/{token}/signature.p7s")]
    [InlineData("POST /api/v1/billing/webhooks/payment-gateway")]
    [InlineData("POST /api/v1/integrations/webhooks/whatsapp/")]
    [InlineData("GET /api/v1/public/unsubscribe/{token}")]
    [InlineData("POST /api/v1/public/unsubscribe/{token}")]
    [InlineData("GET /api/v1/public/feedback/invitations/{token}")]
    [InlineData("GET /api/v1/public/privacy")]
    [InlineData("GET /api/v1/system/info")]
    [InlineData("POST /api/v1/auth/login")]
    [InlineData("POST /api/v1/auth/logout")]
    [InlineData("POST /api/v1/rpm/ingest")]
    [InlineData("GET /api/v1/settings/public")]
    [InlineData("GET /api/v1/clinical/discharge-summaries/{id:guid}/pdf")]
    [InlineData("GET /fhir/r4/{type}/{id}")]
    [InlineData("GET /fhir/r4/{type}")]
    public void What_must_keep_working_has_no_switch(string key) =>
        Endpoints.Value.Single(e => e.Key == key).Requirements.ShouldBeEmpty();

    [Fact]
    public void Remote_patient_monitoring_is_not_switched_off_with_video()
    {
        var rpm = Endpoints.Value.Where(e => e.Route.StartsWith("/api/v1/rpm/", StringComparison.Ordinal)).ToList();

        rpm.ShouldNotBeEmpty();
        rpm.ShouldAllBe(e => e.Requirements.Count == 0);
    }

    private static HttpRequest RequestTo(Listener address, string client)
    {
        var http = new DefaultHttpContext();
        http.Features.Set(new ListenerFeature(address));
        http.Request.Headers["X-HC360-Client"] = client;
        return http.Request;
    }

    private sealed record SwitchedEndpoint(string Key, string Route, IReadOnlyList<RequiresFeatureMetadata> Requirements)
    {
        /// <summary>The feature is required of every request (not only of some, as the portal's refreshes).</summary>
        public bool AlwaysNeeds(string feature) => Requirements.Any(r => r.Feature == feature && r.AppliesTo is null);

        public static IEnumerable<SwitchedEndpoint> Of(RouteEndpoint endpoint)
        {
            var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            var requirements = endpoint.Metadata.GetOrderedMetadata<RequiresFeatureMetadata>();
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            return methods is { Count: > 0 }
                ? methods.Select(method => new SwitchedEndpoint($"{method} {route}", route, requirements))
                : [new SwitchedEndpoint($"ANY {route}", route, requirements)];
        }
    }
}
