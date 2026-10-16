using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Integration log rows written by background work (no signed-in user) are tagged with the facility the work was for,
/// so per-facility health on the integrations dashboard counts only that facility's calls (SRS §5).
/// </summary>
public sealed class IntegrationLogFacilityTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Background_rows_take_the_named_facility_and_the_entry_wins_over_the_scope()
    {
        var integration = "t" + Guid.NewGuid().ToString("N")[..12];
        var main = Guid.CreateVersion7();
        var branch = Guid.CreateVersion7();

        // A scope outside any request, as the outbox and the jobs use: nobody is signed in.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<IIntegrationLogger>();
            await logger.LogAsync(Entry(integration, "none"), Ct);
            await logger.LogAsync(Entry(integration, "entry") with { FacilityId = main }, Ct);
            using (IntegrationLogScope.ForFacility(branch))
            {
                await logger.LogAsync(Entry(integration, "scope"), Ct);
                await logger.LogAsync(Entry(integration, "both") with { FacilityId = main }, Ct);
            }
        }

        await using var read = server.Services.CreateAsyncScope();
        var rows = await read.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<IntegrationLog>().AsNoTracking()
            .Where(l => l.Integration == integration)
            .ToDictionaryAsync(l => l.Operation, l => l.FacilityId, Ct);
        rows["none"].ShouldBeNull();
        rows["entry"].ShouldBe(main);
        rows["scope"].ShouldBe(branch);
        rows["both"].ShouldBe(main);
    }

    private static IntegrationLogEntry Entry(string integration, string operation) =>
        new(integration, "test", operation, IntegrationDirection.Outbound, true);
}
