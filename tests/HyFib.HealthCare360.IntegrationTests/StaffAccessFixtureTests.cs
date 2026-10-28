using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The staff app's role-access check (src/web/apps/staff/src/test/roleAccess.ts) opens every menu link and record screen
/// as every built-in role and fails for a screen that asks the server for something the role may not read, or shows a
/// button that sends what the role may not do, and for a screen that calls a path or method the server does not have.
/// It reads the roles' permissions and the API's routes with their permission rules from <c>serverAccess.json</c>, kept
/// beside it; this test fails when that file no longer says what the server does. Write it again with
/// HC360_WRITE_ACCESS_FIXTURE=1.
/// </summary>
public sealed class StaffAccessFixtureTests(HospitalServer server)
{
    private const string WriteVariable = "HC360_WRITE_ACCESS_FIXTURE";

    /// <summary>The rule of a route that asks for no permission: any signed-in user, or anyone.</summary>
    private const string NoPermission = "-";

    [Fact]
    public void The_staff_apps_copy_of_roles_and_read_rules_matches_the_server()
    {
        var file = Path.Combine(RepositoryRoot(), "src", "web", "apps", "staff", "src", "test", "serverAccess.json");
        var current = Describe();
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(file, current);
        }

        var committed = File.Exists(file) ? File.ReadAllText(file).ReplaceLineEndings("\n") : "";
        committed.ShouldBe(current,
            $"{Path.GetFileName(file)} no longer matches the server's built-in roles, or its routes and the permissions they ask for. " +
            $"Run StaffAccessFixtureTests with {WriteVariable}=1 to write it again, and commit it.");
    }

    /// <summary>
    /// Clinical note templates, order sets and drug-safety rules are kept by whoever may manage them, and radiology
    /// procedures by the masters' keepers: they can read the lists they keep without being able to open a patient's chart.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/emr/templates")]
    [InlineData("/api/v1/clinical/order-sets")]
    [InlineData("/api/v1/clinical/drug-interactions")]
    [InlineData("/api/v1/clinical/dose-limits")]
    [InlineData("/api/v1/radiology/procedures")]
    public async Task A_hospital_administrator_reads_the_catalogues_they_keep(string path)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        using var response = await admin.SendRawAsync(HttpMethod.Get, path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The built-in roles' permissions (as seeded), and every route under /api with its permission rules: each GET as
    /// "path rules", each write (POST, PUT, PATCH, DELETE) as "METHOD path rules", and "-" for a route that asks for no
    /// permission, so that a call to a route the server does not have is told apart from one that needs none.
    /// </summary>
    private string Describe()
    {
        var registry = server.Services.GetRequiredService<PermissionRegistry>();
        var roles = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, _) in SystemRoles.All)
        {
            roles[name] = name == SystemRoles.SuperAdmin
                ? [PermissionRegistry.Wildcard]
                : [.. registry.All.Where(d => d.DefaultRoles?.Contains(name) == true).Select(d => d.Name).Order(StringComparer.Ordinal)];
        }

        var reads = new SortedSet<string>(StringComparer.Ordinal);
        var writes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in server.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var path = "/" + endpoint.RoutePattern.RawText?.Trim('/');
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            var rules = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .Where(p => p?.StartsWith(PermissionPolicyProvider.Prefix, StringComparison.Ordinal) == true)
                .Select(p => p![PermissionPolicyProvider.Prefix.Length..])
                .ToArray();
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                continue;
            }

            var rule = rules.Length == 0 ? NoPermission : string.Join(',', rules);
            if (methods.Contains("GET"))
            {
                reads.Add($"{path} {rule}");
            }

            foreach (var method in methods.Where(m => m is "POST" or "PUT" or "PATCH" or "DELETE"))
            {
                writes.Add($"{method} {path} {rule}");
            }
        }

        return JsonSerializer.Serialize(new { roles, reads, writes }, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

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
