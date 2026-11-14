using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// docs/security/ROLES.md lists the built-in roles, what each is for, what each may do on a new installation and how many
/// of the API's endpoints that opens, and the permissions no built-in role holds: written from the code (the built-in
/// roles, the modules' permissions and the roles they are given by default, the sign-in policy's defaults and every
/// endpoint's permission rule), never by hand, so it cannot say something the server does not do. This test fails when
/// the page no longer matches the code; run it with HC360_WRITE_ROLES_INVENTORY=1 to write the part between its markers
/// again, and commit it. What a hospital changed since (roles of its own, permissions given or taken back, roles turned
/// off) is on its Roles and permissions screen and in its audit trail.
/// </summary>
public sealed class RolesInventoryTests
{
    private const string WriteVariable = "HC360_WRITE_ROLES_INVENTORY";
    private const string Start = "<!-- roles-inventory:start: written from the code by RolesInventoryTests; do not edit by hand -->";
    private const string End = "<!-- roles-inventory:end -->";

    [Fact]
    public void The_roles_page_says_what_the_code_does()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "security", "ROLES.md");
        var page = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = page.IndexOf(Start, StringComparison.Ordinal);
        var end = page.IndexOf(End, StringComparison.Ordinal);
        (start >= 0 && end > start).ShouldBeTrue($"{Path.GetFileName(path)} must keep its two markers: {Start} and {End}");

        var current = page[..(start + Start.Length)] + "\n\n" + Inventory() + "\n" + page[end..];
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(path, current);
            page = current;
        }

        page.ShouldBe(current,
            $"{Path.GetFileName(path)} no longer says what the code does: a role, a permission, a default grant or an endpoint's rule changed. " +
            $"Run RolesInventoryTests with {WriteVariable}=1 to write it again, and commit it.");
    }

    /// <summary>The generated part of the page, from the code as it is.</summary>
    private static string Inventory()
    {
        var app = EndpointAuthorizationTests.App.Value;
        var registry = app.Services.GetRequiredService<PermissionRegistry>();
        var security = new SecurityOptions();
        var definitions = registry.All.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var defaults = SystemRoles.All.ToDictionary(
            r => r.Name,
            r => r.Name == SystemRoles.SuperAdmin
                ? new SortedSet<string>([PermissionRegistry.Wildcard], StringComparer.Ordinal)
                : new SortedSet<string>(definitions.Where(d => d.DefaultRoles?.Contains(r.Name) == true).Select(d => d.Name), StringComparer.Ordinal),
            StringComparer.Ordinal);
        var rules = EndpointRules(app);
        var guarded = rules.Count;

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"### The {SystemRoles.All.Count} built-in roles");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{definitions.Count} permissions are declared. {guarded} of the API's endpoints ask for one or more of them; the column *Endpoints* counts those the role's default permissions open.");
        text.AppendLine();
        text.AppendLine("| Role | Responsibility | Default permissions | Endpoints | Two-step verification | Can be turned off |");
        text.AppendLine("|---|---|---:|---:|---|---|");
        foreach (var (name, description) in SystemRoles.All)
        {
            var held = defaults[name];
            var opens = rules.Count(rule => Opens(held, rule.Groups));
            var mfa = !security.EnforceMfa ? "No"
                : security.MfaRequiredRoles.Contains(name, StringComparer.OrdinalIgnoreCase) ? "Required (role)"
                : security.MfaForAdministration && held.Any(PlatformPermissions.IsAdministrative) ? "Required (administers)"
                : "If the user sets it up";
            var count = held.Contains(PermissionRegistry.Wildcard) ? "all (`*`)" : held.Count.ToString(CultureInfo.InvariantCulture);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| `{name}` | {Cell(description)} | {count} | {opens} | {mfa} | {(SystemRoles.AlwaysInUse.Contains(name) ? "No, always in use" : "Yes")} |");
        }

        text.AppendLine();
        text.AppendLine("### What each role may do on a new installation");
        text.AppendLine();
        text.AppendLine("Each permission is listed with its name, by the area that declares it.");
        foreach (var (name, description) in SystemRoles.All)
        {
            var held = defaults[name];
            text.AppendLine();
            if (held.Contains(PermissionRegistry.Wildcard))
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- **`{name}`**: every permission (`*`), including those added by future updates.");
                continue;
            }

            text.AppendLine("<details>");
            text.AppendLine(CultureInfo.InvariantCulture, $"<summary><strong>{name}</strong>: {Html(description)} ({held.Count} permissions)</summary>");
            text.AppendLine();
            text.AppendLine("| Area | May |");
            text.AppendLine("|---|---|");
            foreach (var area in definitions.Where(d => held.Contains(d.Name)).GroupBy(d => d.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Cell(area.Key)} | {string.Join("; ", area.Select(d => $"{Cell(d.Description)} (`{d.Name}`)"))} |");
            }

            text.AppendLine();
            text.AppendLine("</details>");
        }

        var unheld = definitions.Where(d => d.DefaultRoles is not { Length: > 0 }).ToList();
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"### The {unheld.Count} permissions no built-in role holds by default");
        text.AppendLine();
        text.AppendLine("Only the Super Administrator has them on a new installation, until an administrator who holds them gives them to a role.");
        text.AppendLine();
        text.AppendLine("| Permission | Area | What it allows | Endpoints |");
        text.AppendLine("|---|---|---|---:|");
        foreach (var definition in unheld)
        {
            var opens = rules.Count(rule => rule.Groups.Any(group => group.Contains(definition.Name, StringComparer.Ordinal)));
            text.AppendLine(CultureInfo.InvariantCulture, $"| `{definition.Name}` | {Cell(definition.Group)} | {Cell(definition.Description)} | {opens} |");
        }

        return text.ToString();
    }

    /// <summary>
    /// Every endpoint that asks for a permission, by method and route, with its rule: groups that must all be satisfied, each
    /// by any one of its permissions (<c>.RequirePermission</c> and <c>.RequireAnyPermission</c>, on the endpoint and its
    /// groups).
    /// </summary>
    private static List<(string Key, List<string[]> Groups)> EndpointRules(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                List<string[]> groups =
                [
                    .. endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                        .Where(a => a.Policy?.StartsWith(PermissionPolicyProvider.Prefix, StringComparison.Ordinal) == true)
                        .Select(a => a.Policy![PermissionPolicyProvider.Prefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    .. endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()
                        .SelectMany(p => p.Requirements.OfType<PermissionRequirement>())
                        .Select(r => r.AnyOf.ToArray()),
                ];
                var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
                return methods.Select(method => (Key: $"{method} {route}", Groups: groups));
            })
            .Where(rule => rule.Groups.Count > 0)
            .DistinctBy(rule => rule.Key, StringComparer.Ordinal)];

    /// <summary>Whether permissions <paramref name="held"/> open an endpoint whose rule is <paramref name="groups"/>.</summary>
    private static bool Opens(IReadOnlySet<string> held, IEnumerable<string[]> groups) =>
        held.Contains(PermissionRegistry.Wildcard) || groups.All(group => group.Any(held.Contains));

    /// <summary>Text for a Markdown table cell.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    /// <summary>Text inside an HTML element of the page.</summary>
    private static string Html(string text) => System.Net.WebUtility.HtmlEncode(text);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (HyFib.HealthCare360.slnx) was not found above the test's folder.");
    }
}
