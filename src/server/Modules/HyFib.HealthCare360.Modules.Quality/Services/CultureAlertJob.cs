using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// Every ten minutes, looks at the cultures verified in the last three days for one that grew an organism on a patient with
/// a device, or after an operation, and raises a suspected HAI case with an alert to the infection control team
/// (<see cref="HaiService.ScanCulturesAsync"/>). The laboratory raises no event when a result is verified, so the alert comes
/// within minutes rather than at once. Runs only on job-running nodes, under a cluster-wide lock; a result raises a case once.
/// </summary>
internal sealed class CultureAlertJob(IServiceScopeFactory scopeFactory, TimeProvider time, IOptions<PlatformOptions> options, ILogger<CultureAlertJob> logger)
    : BackgroundService
{
    internal const long LockKey = 4_247_702;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), time);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(stoppingToken))
                {
                    continue;
                }

                var raised = await scope.ServiceProvider.GetRequiredService<HaiService>().ScanCulturesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
                if (raised > 0)
                {
                    logger.LogInformation("Raised {Count} suspected HAI cases from positive cultures", raised);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The culture scan for HAI surveillance failed; it will be retried");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
