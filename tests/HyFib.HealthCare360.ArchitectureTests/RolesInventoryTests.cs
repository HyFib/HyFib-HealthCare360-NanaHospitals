using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Approvals;
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
/// endpoint's permission rule), never by hand, so it cannot say something the server does not do. docs/security/roles.json
/// holds the same inventory as data, with the kinds of change a second person approves, for tools and the shareable roles
/// page. The inventory is read from the code once (<see cref="Read"/>) and both files are written from that one reading,
/// so they cannot disagree. This test fails when either no longer matches the code; run it with
/// HC360_WRITE_ROLES_INVENTORY=1 to write the part of the page between its markers and the whole of roles.json again,
/// and commit them. What a hospital changed since (roles of its own, permissions given or taken back, roles turned off)
/// is on its Roles and permissions screen and in its audit trail.
/// </summary>
public sealed class RolesInventoryTests
{
    private const string WriteVariable = "HC360_WRITE_ROLES_INVENTORY";
    private const string Start = "<!-- roles-inventory:start: written from the code by RolesInventoryTests; do not edit by hand -->";
    private const string End = "<!-- roles-inventory:end -->";

    /// <summary>The inventory, read from the code once for every file written from it.</summary>
    private static readonly Lazy<Inventory> Current = new(Read);

    /// <summary>
    /// roles.json is the same on every platform: \n line endings (the serializer's default is the platform's), and
    /// apostrophes and non-ASCII letters written as they are, so that a change reads as a short diff in review.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private enum TwoStep
    {
        NotEnforced,
        RequiredByRole,
        RequiredToAdminister,
        Optional,
    }

    [Fact]
    public void The_roles_page_says_what_the_code_does()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "security", "ROLES.md");
        var page = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = page.IndexOf(Start, StringComparison.Ordinal);
        var end = page.IndexOf(End, StringComparison.Ordinal);
        (start >= 0 && end > start).ShouldBeTrue($"{Path.GetFileName(path)} must keep its two markers: {Start} and {End}");

        var current = page[..(start + Start.Length)] + "\n\n" + Markdown(Current.Value) + "\n" + page[end..];
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(path, current);
            page = current;
        }

        page.ShouldBe(current,
            $"{Path.GetFileName(path)} no longer says what the code does: a role, a permission, a default grant or an endpoint's rule changed. " +
            $"Run RolesInventoryTests with {WriteVariable}=1 to write it again, and commit it.");
    }

    [Fact]
    public void The_roles_data_says_what_the_code_does()
    {
        var inventory = Current.Value;
        inventory.ApprovalProblems.ShouldBeEmpty(
            "roles.json lists each kind of approval once, with the permission its approver holds: a kind registered twice with " +
            "different titles or approvers would be applied by whichever the approvals service finds first, and an approver " +
            "permission the code does not declare could be given to nobody");

        var path = Path.Combine(RepositoryRoot(), "docs", "security", "roles.json");
        var file = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : string.Empty;
        var current = Json(inventory);
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(path, current);
            file = current;
        }

        file.ShouldBe(current,
            $"{Path.GetFileName(path)} no longer says what the code does: a role, a permission, a default grant, an endpoint's rule or an approval changed. " +
            $"Run RolesInventoryTests with {WriteVariable}=1 to write it again, and commit it.");
    }

    /// <summary>
    /// Reads the inventory from the server as Program.cs composes it: the built-in roles in their order, each with the
    /// permissions it gets on a new installation, the endpoints those open, and what the sign-in policy's defaults ask of
    /// it; every permission, by name, with its area, the roles that get it and the endpoints whose rule names it; and every
    /// kind of change a second person approves, with the permission its approver holds. The module scan registers an
    /// approval action, and a module may register it again, so each kind is listed once.
    /// </summary>
    private static Inventory Read()
    {
        var app = EndpointAuthorizationTests.App.Value;
        var registry = app.Services.GetRequiredService<PermissionRegistry>();
        var security = new SecurityOptions();
        var definitions = registry.All.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var rules = EndpointRules(app);

        List<Role> roles =
        [
            .. SystemRoles.All.Select(role =>
            {
                var (name, description) = role;
                var held = name == SystemRoles.SuperAdmin
                    ? new SortedSet<string>([PermissionRegistry.Wildcard], StringComparer.Ordinal)
                    : new SortedSet<string>(definitions.Where(d => d.DefaultRoles?.Contains(name) == true).Select(d => d.Name), StringComparer.Ordinal);
                var mfa = !security.EnforceMfa ? TwoStep.NotEnforced
                    : security.MfaRequiredRoles.Contains(name, StringComparer.OrdinalIgnoreCase) ? TwoStep.RequiredByRole
                    : security.MfaForAdministration && held.Any(PlatformPermissions.IsAdministrative) ? TwoStep.RequiredToAdminister
                    : TwoStep.Optional;
                return new Role(name, description, [.. held], rules.Count(rule => Opens(held, rule.Groups)), mfa, !SystemRoles.AlwaysInUse.Contains(name));
            }),
        ];

        List<Permission> permissions =
        [
            .. definitions.Select(d => new Permission(
                d.Name,
                d.Group,
                d.Description,
                [.. (d.DefaultRoles ?? []).Order(StringComparer.Ordinal)],
                PlatformPermissions.IsAdministrative(d.Name),
                rules.Count(rule => rule.Groups.Any(group => group.Contains(d.Name, StringComparer.Ordinal))))),
        ];

        using var scope = app.Services.CreateScope();
        List<Approval> registered =
        [
            .. scope.ServiceProvider.GetServices<IApprovalAction>().Select(a => new Approval(a.Kind, a.Title, a.ApprovePermission)),
        ];
        var declared = permissions.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        List<string> problems =
        [
            .. registered.GroupBy(a => a.Kind, StringComparer.Ordinal).Where(g => g.Distinct().Count() > 1)
                .Select(g => $"{g.Key}: registered with different titles or approvers"),
            .. registered.Where(a => !declared.Contains(a.ApprovePermission)).Select(a => $"{a.Kind}: approver permission {a.ApprovePermission} is not declared"),
        ];
        List<Approval> approvals = [.. registered.DistinctBy(a => a.Kind, StringComparer.Ordinal).OrderBy(a => a.Kind, StringComparer.Ordinal)];

        return new Inventory(roles, permissions, rules.Count, approvals, [.. problems.Distinct(StringComparer.Ordinal)], security);
    }

    /// <summary>docs/security/roles.json: the inventory as data. ROLES.md, "The same data as JSON", says what each field means.</summary>
    private static string Json(Inventory inventory)
    {
        var document = new
        {
            About = "Built-in roles and permissions of a new HealthCare360 installation, written from the code by RolesInventoryTests " +
                "(tests/HyFib.HealthCare360.ArchitectureTests); do not edit by hand. ROLES.md beside this file says what each field means.",
            Version = 1,
            Totals = new
            {
                Roles = inventory.Roles.Count,
                Permissions = inventory.Permissions.Count,
                inventory.GuardedEndpoints,
                ApprovalKinds = inventory.Approvals.Count,
            },
            Security = new { inventory.Security.EnforceMfa, inventory.Security.MfaForAdministration, inventory.Security.MfaRequiredRoles },
            Roles = inventory.Roles.Select(r => new { r.Name, r.Description, r.DefaultPermissions, r.Endpoints, Mfa = MfaCode(r.Mfa), r.CanBeTurnedOff }),
            Permissions = inventory.Permissions.Select(p => new { p.Name, p.Area, p.Description, p.DefaultRoles, p.Administrative, p.Endpoints }),
            Approvals = inventory.Approvals.Select(a => new { a.Kind, a.Title, a.ApprovePermission }),
        };
        return JsonSerializer.Serialize(document, JsonOptions) + "\n";
    }

    /// <summary>The generated part of ROLES.md.</summary>
    private static string Markdown(Inventory inventory)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"### The {inventory.Roles.Count} built-in roles");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{inventory.Permissions.Count} permissions are declared. {inventory.GuardedEndpoints} of the API's endpoints ask for one or more of them; the column *Endpoints* counts those the role's default permissions open.");
        text.AppendLine();
        text.AppendLine("| Role | Responsibility | Default permissions | Endpoints | Two-step verification | Can be turned off |");
        text.AppendLine("|---|---|---:|---:|---|---|");
        foreach (var role in inventory.Roles)
        {
            var count = role.HoldsAll ? "all (`*`)" : role.DefaultPermissions.Count.ToString(CultureInfo.InvariantCulture);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| `{role.Name}` | {Cell(role.Description)} | {count} | {role.Endpoints} | {MfaLabel(role.Mfa)} | {(role.CanBeTurnedOff ? "Yes" : "No, always in use")} |");
        }

        text.AppendLine();
        text.AppendLine("### What each role may do on a new installation");
        text.AppendLine();
        text.AppendLine("Each permission is listed with its name, by the area that declares it.");
        foreach (var role in inventory.Roles)
        {
            text.AppendLine();
            if (role.HoldsAll)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- **`{role.Name}`**: every permission (`*`), including those added by future updates.");
                continue;
            }

            text.AppendLine("<details>");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"<summary><strong>{role.Name}</strong>: {Html(role.Description)} ({role.DefaultPermissions.Count} permissions)</summary>");
            text.AppendLine();
            text.AppendLine("| Area | May |");
            text.AppendLine("|---|---|");
            var held = role.DefaultPermissions.ToHashSet(StringComparer.Ordinal);
            foreach (var area in inventory.Permissions.Where(p => held.Contains(p.Name)).GroupBy(p => p.Area).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Cell(area.Key)} | {string.Join("; ", area.Select(p => $"{Cell(p.Description)} (`{p.Name}`)"))} |");
            }

            text.AppendLine();
            text.AppendLine("</details>");
        }

        var unheld = inventory.Permissions.Where(p => p.DefaultRoles.Count == 0).ToList();
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"### The {unheld.Count} permissions no built-in role holds by default");
        text.AppendLine();
        text.AppendLine("Only the Super Administrator has them on a new installation, until an administrator who holds them gives them to a role.");
        text.AppendLine();
        text.AppendLine("| Permission | Area | What it allows | Endpoints |");
        text.AppendLine("|---|---|---|---:|");
        foreach (var permission in unheld)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| `{permission.Name}` | {Cell(permission.Area)} | {Cell(permission.Description)} | {permission.Endpoints} |");
        }

        // AppendLine ends lines with the platform's newline, \r\n on Windows; the page is kept with \n (.gitattributes).
        return text.ToString().ReplaceLineEndings("\n");
    }

    private static string MfaLabel(TwoStep mfa) => mfa switch
    {
        TwoStep.NotEnforced => "No",
        TwoStep.RequiredByRole => "Required (role)",
        TwoStep.RequiredToAdminister => "Required (administers)",
        TwoStep.Optional => "If the user sets it up",
        _ => throw new ArgumentOutOfRangeException(nameof(mfa), mfa, null),
    };

    private static string MfaCode(TwoStep mfa) => mfa switch
    {
        TwoStep.NotEnforced => "not-enforced",
        TwoStep.RequiredByRole => "required-by-role",
        TwoStep.RequiredToAdminister => "required-to-administer",
        TwoStep.Optional => "optional",
        _ => throw new ArgumentOutOfRangeException(nameof(mfa), mfa, null),
    };

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

    /// <summary>A built-in role: its name and job, its default permissions (<c>*</c> for all), the endpoints those open, and whether it can be turned off.</summary>
    private sealed record Role(string Name, string Description, IReadOnlyList<string> DefaultPermissions, int Endpoints, TwoStep Mfa, bool CanBeTurnedOff)
    {
        public bool HoldsAll => DefaultPermissions.Contains(PermissionRegistry.Wildcard);
    }

    /// <summary>A permission: its area and meaning, the built-in roles given it by default, and the endpoints whose rule names it.</summary>
    private sealed record Permission(string Name, string Area, string Description, IReadOnlyList<string> DefaultRoles, bool Administrative, int Endpoints);

    /// <summary>A kind of change a second person approves: its title, and the permission its approver must hold.</summary>
    private sealed record Approval(string Kind, string Title, string ApprovePermission);

    /// <summary>
    /// What the code declares and decides about roles, read once, with anything that would make the approvals ambiguous
    /// (<see cref="ApprovalProblems"/>) and the sign-in policy's defaults.
    /// </summary>
    private sealed record Inventory(
        IReadOnlyList<Role> Roles,
        IReadOnlyList<Permission> Permissions,
        int GuardedEndpoints,
        IReadOnlyList<Approval> Approvals,
        IReadOnlyList<string> ApprovalProblems,
        SecurityOptions Security);
}
