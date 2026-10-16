using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS DED-17: System health shows each integration as the integrations dashboard does, through the Platform contract the
/// Integrations project implements, and warns while one that is switched on is failing.
/// </summary>
public sealed class SystemHealthIntegrationsTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task System_health_says_when_an_integration_that_is_switched_on_is_failing()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await SaveAsync(admin, "integrations.icd", """{"enabled":true,"provider":"simulator"}""");
        try
        {
            // ICD lookup worked earlier, and its latest call failed: after any success other tests logged.
            await LogAsync(success: true, DateTimeOffset.UtcNow.AddMinutes(-1));
            await LogAsync(success: false, DateTimeOffset.UtcNow);

            var health = await admin.GetAsync<HealthIntegrations>("/api/v1/admin/system/health");

            var icd = health.Integrations.Single(i => i.Name == "icd");
            icd.ShouldSatisfyAllConditions(
                i => (i.DisplayName, i.Enabled, i.Configured, i.Health).ShouldBe(("ICD lookup", true, true, "failing")),
                i => i.LastSuccessAt.ShouldNotBeNull().ShouldBeLessThan(i.LastFailureAt.ShouldNotBeNull()),
                i => i.FailuresLast24Hours.ShouldBeGreaterThanOrEqualTo(1));
            var warning = health.Warnings.Where(w => w.Code == "integrations.failing").ShouldHaveSingleItem();
            warning.Severity.ShouldBe("warning");
            warning.Message.ShouldContain("ICD lookup");

            // Switched off, it no longer needs attention here.
            await SaveAsync(admin, "integrations.icd", """{"enabled":false,"provider":"disabled"}""");
            var off = await admin.GetAsync<HealthIntegrations>("/api/v1/admin/system/health");
            off.Integrations.Single(i => i.Name == "icd").Enabled.ShouldBeFalse();
            off.Warnings.ShouldNotContain(w => w.Code == "integrations.failing" && w.Message.Contains("ICD lookup"));
        }
        finally
        {
            await SaveAsync(admin, "integrations.icd", """{"enabled":false,"provider":"disabled"}""");
        }
    }

    private async Task LogAsync(bool success, DateTimeOffset at)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Set<IntegrationLog>().Add(new IntegrationLog
        {
            CreatedAt = at,
            Integration = "icd",
            Provider = "simulator",
            Operation = "search",
            Success = success,
            Error = success ? null : "The training list could not be read.",
        });
        await db.SaveChangesAsync(Ct);
    }

    private static Task SaveAsync(ApiClient admin, string key, string json) =>
        admin.PutAsync($"/api/v1/admin/settings/{key}", new SaveSettingRequest(JsonNode.Parse(json), null, false, null));

    private sealed record HealthIntegrations(IReadOnlyList<IntegrationHealth> Integrations, IReadOnlyList<HealthWarning> Warnings);
}
