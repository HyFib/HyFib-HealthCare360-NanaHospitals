using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// ASVS V3.5.4: the patient portal's address answers for the portal alone (<see cref="ListenerPaths"/>), and for the services
/// outside the hospital that call it. Every endpoint the portal needs answers there, and nothing that needs a staff
/// permission, or only a signed-in user, does: script on a portal page finds no staff endpoint to call there, whatever it
/// gets hold of.
/// </summary>
public sealed class PortalAddressTests
{
    [Fact]
    public void Every_endpoint_of_the_portal_answers_on_its_address()
    {
        var unreachable = Endpoints()
            .Where(e => e.Permissions.Contains(PatientsPermissions.Portal) && ListenerPaths.Decide(Listener.Portal, e.Route) != ListenerRoute.Serve)
            .Select(e => e.Route).Distinct().Order().ToList();

        unreachable.ShouldBeEmpty(
            "These portal endpoints do not answer on the portal's address. Put them under /api/v1/portal, or add them to " +
            "ListenerPaths:\n" + string.Join('\n', unreachable));
    }

    [Fact]
    public void What_answers_on_the_portals_address_is_the_portals_own_or_open_to_anyone()
    {
        var staff = Endpoints()
            .Where(e => ListenerPaths.Decide(Listener.Portal, e.Route) == ListenerRoute.Serve)
            .Where(e => !e.Anonymous && !(e.Permissions.Count > 0 && e.Permissions.All(p => p == PatientsPermissions.Portal)))
            .Select(e => e.Route).Distinct().Order().ToList();

        staff.ShouldBeEmpty(
            "These endpoints answer on the portal's address but need a staff permission or only a signed-in user. Serve them " +
            "on the staff app's address only (ListenerPaths):\n" + string.Join('\n', staff));
    }

    /// <summary>
    /// The portal's address is the one a hospital opens to the internet (DEPLOYMENT.md), so the ABDM gateway's callbacks, the
    /// records other providers push and consent managers' events must answer there: on the staff app's address alone, ABDM
    /// would work only with staff sign-in open to the internet.
    /// </summary>
    [Fact]
    public void Every_call_the_abdm_gateway_providers_and_consent_managers_make_answers_on_the_portals_address()
    {
        var callbacks = Endpoints()
            .Where(e => e.Route.StartsWith("/api/v3/", StringComparison.Ordinal) || e.Route.StartsWith("/api/v1/consent-managers/", StringComparison.Ordinal))
            .ToList();

        callbacks.Count.ShouldBeGreaterThanOrEqualTo(14, "the twelve callbacks, the data push and consent managers' events");
        callbacks.Where(e => ListenerPaths.Decide(Listener.Portal, e.Route) != ListenerRoute.Serve).Select(e => e.Route).Distinct().Order()
            .ShouldBeEmpty("Services outside the hospital call these, but they do not answer on the portal's address (ListenerPaths)");
    }

    /// <summary>
    /// Home monitoring devices upload their readings from patients' homes, so their endpoint answers on the portal's address,
    /// the one opened to the internet: on the staff app's address alone, no reading would arrive. It takes no session (each
    /// device proves itself with its own key), as everything else that answers there without the portal's permission.
    /// </summary>
    [Fact]
    public void Home_monitoring_devices_upload_on_the_portals_address()
    {
        var ingest = Endpoints().Where(e => e.Route == "/api/v1/rpm/ingest").ToList();

        ingest.ShouldNotBeEmpty("the home monitoring devices' upload endpoint");
        ingest.ShouldAllBe(e => e.Anonymous && ListenerPaths.Decide(Listener.Portal, e.Route) == ListenerRoute.Serve);
    }

    /// <summary>
    /// SMART on FHIR's authorization hands the browser to the staff app's sign-in, and the FHIR API reads as a staff account
    /// (a session or a token issued for one): neither answers where the portal's third-party script runs.
    /// </summary>
    [Fact]
    public void Nothing_of_the_fhir_api_or_smart_on_fhir_answers_on_the_portals_address()
    {
        var smart = Endpoints()
            .Where(e => e.Route.StartsWith("/fhir/", StringComparison.Ordinal) || e.Route.StartsWith("/oauth2/", StringComparison.Ordinal)
                        || e.Route.StartsWith("/api/v1/smart/", StringComparison.Ordinal))
            .ToList();

        smart.ShouldNotBeEmpty();
        smart.Where(e => ListenerPaths.Decide(Listener.Portal, e.Route) != ListenerRoute.NotFound).Select(e => e.Route).Distinct().Order()
            .ShouldBeEmpty("These answer on the portal's address; serve them on the staff app's only (ListenerPaths)");
    }

    private static IEnumerable<(string Route, IReadOnlyList<string> Permissions, bool Anonymous)> Endpoints() =>
        ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
                (IReadOnlyList<string>)[.. endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(a => a.Policy)
                    .Where(policy => policy?.StartsWith(PermissionPolicyProvider.Prefix, StringComparison.Ordinal) == true)
                    .SelectMany(policy => policy![PermissionPolicyProvider.Prefix.Length..].Split('|'))],
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null));
}
