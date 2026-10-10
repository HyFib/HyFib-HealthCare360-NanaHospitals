using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// ASVS V3.5.4: the patient portal's address answers for the portal alone (<see cref="ListenerPaths"/>). Every endpoint the
/// portal needs answers there, and nothing that needs a staff permission, or only a signed-in user, does: script on a
/// portal page finds no staff endpoint to call there, whatever it gets hold of.
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
