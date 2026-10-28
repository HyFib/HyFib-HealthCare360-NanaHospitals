using HyFib.HealthCare360.Platform.Networking;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the System health alerts (<see cref="SystemHealthAlerts"/>) leave to others. AuditSealTests has what they tell: a
/// broken audit chain, once while it lasts and again when it comes back.
/// </summary>
public sealed class SystemHealthAlertTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The WiFi monitor tells a drop, a lost adapter or an insecure network as it happens (DED-06), by its own rules, and
    /// not at all where the hospital turned it off. The System health alerts leave WiFi to it, so that one drop is not told
    /// twice, in the app and to the IT on-call number.
    /// </summary>
    [Fact]
    public async Task A_lost_WiFi_adapter_is_left_to_the_WiFi_monitor_to_tell()
    {
        var wifi = server.Services.GetRequiredService<WifiLinkState>();
        using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
        wifi.Update(null, DateTimeOffset.UtcNow, "Server Node lost its WiFi adapter: Windows shows none (disabled or removed).");
        try
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                (await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildAsync(Ct)).Warnings
                    .ShouldContain(w => w.Code == "wifi.adapter_missing" && w.Severity == "critical");
            }

            (await alerts.CheckAsync(Ct)).ShouldNotContain(w => w.Code.StartsWith("wifi.", StringComparison.Ordinal));
        }
        finally
        {
            // As the monitor leaves it on a server without WiFi.
            wifi.Update(null, DateTimeOffset.UtcNow);
        }
    }
}
