using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// SRS REL-04: optimistic concurrency on every editable record; a conflict is shown, never silently overwritten. Every
/// edit (PUT or PATCH) says which version of the record it changes. Its request has one of these:
/// <list type="bullet">
/// <item>a <c>uint Version</c> the client must send;</item>
/// <item>a <c>uint? Version</c>, shared with the create, that the edit requires (<see cref="VersionRequired"/>);</item>
/// <item>a <c>uint? Version</c> that is empty only on the save that creates the record (<see cref="CreatedByFirstSave"/>).</item>
/// </list>
/// </summary>
public sealed class OptimisticConcurrencyTests
{
    /// <summary>Edits that guard against a lost update another way, and how.</summary>
    private static readonly Dictionary<string, string> GuardedOtherwise = new()
    {
        ["PUT /api/v1/admin/number-series/{key}"] =
            "Sends the format and reset it started from (ExpectedFormat, ExpectedResetPolicy). Every number issued moves the "
            + "series' version on, so a version would refuse saves made while bills are being numbered.",
        ["PUT /api/v1/integrations/lab/connections/{code}"] =
            "Sends the version of the analyser list it loaded (ListVersion): every analyser's connection is kept in one setting.",
        ["PUT /api/v1/hr/roster"] =
            "Sends the entries it replaces as the grid showed them (Replacing: id and version). A cell holds several entries.",
        ["PUT /api/v1/portal/communication-preferences"] =
            "Sends only the choice the patient just made (each switch saves at once). A consent is the patient's latest "
            + "choice, so there is nothing of anyone else's to overwrite.",
    };

    [Fact]
    public void Every_edit_says_which_version_it_changes()
    {
        var unversioned = Edits()
            .Where(e => !GuardedOtherwise.ContainsKey(e.Route))
            .Where(e => e.Version?.PropertyType != typeof(uint)
                && !(e.Version?.PropertyType == typeof(uint?) && (e.Endpoint.Metadata.GetMetadata<VersionRequired>() is not null
                    || e.Endpoint.Metadata.GetMetadata<CreatedByFirstSave>() is not null)))
            .Select(e => $"{e.Route} ({e.Body?.Name ?? "no body"}{(e.Version is { } v ? ", Version " + v.PropertyType.Name : string.Empty)})")
            .Order()
            .ToList();

        unversioned.ShouldBeEmpty(
            $"{unversioned.Count} edits do not require the version they change. Give the request a Version, and mark an optional "
            + "one .RequireVersion(), or .FirstSaveCreates() when the first save creates the record:\n" + string.Join('\n', unversioned));
    }

    [Fact]
    public void Each_edit_that_guards_otherwise_is_still_an_edit_without_a_version()
    {
        var edits = Edits().ToDictionary(e => e.Route);

        GuardedOtherwise.Keys.Where(route => !edits.TryGetValue(route, out var edit) || edit.Version is not null)
            .ShouldBeEmpty("These exceptions are out of date: the edit is gone or now has a version.");
    }

    [Fact]
    public void A_required_version_is_one_the_create_leaves_empty()
    {
        // .RequireVersion() on a request whose Version is already required, or that has none, protects nothing.
        Edits().Where(e => e.Endpoint.Metadata.GetMetadata<VersionRequired>() is not null && e.Version?.PropertyType != typeof(uint?))
            .Select(e => e.Route)
            .ShouldBeEmpty();
    }

    private static IEnumerable<(string Route, Type? Body, System.Reflection.PropertyInfo? Version, RouteEndpoint Endpoint)> Edits() =>
        ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Any(m => m is "PUT" or "PATCH") == true)
            .Select(e =>
            {
                var body = e.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
                return ($"{string.Join(',', e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}",
                    body, body?.GetProperty("Version"), e);
            });
}
