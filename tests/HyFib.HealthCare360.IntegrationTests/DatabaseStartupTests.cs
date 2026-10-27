using HyFib.HealthCare360.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The database is brought up to date as the host's first hosted service, not before the host starts: Windows gives a
/// service 30 seconds to report in, and the host reports in (its lifetime) before any hosted service starts. Migrations,
/// the backup before them, or PostgreSQL still starting after a power cut take longer than that, and a service that
/// does not report in is marked failed without the restart-on-failure Windows does for one that stops.
/// </summary>
public sealed class DatabaseStartupTests
{
    [Fact]
    public void The_database_is_brought_up_to_date_before_any_other_hosted_service_starts()
    {
        var services = new ServiceCollection();
        services.AddHostedService<Other>();
        services.AddDatabaseStartup();
        services.AddHostedService<Other>();

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().First().ShouldBeOfType<DatabaseStartup>();
    }

    private sealed class Other : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
