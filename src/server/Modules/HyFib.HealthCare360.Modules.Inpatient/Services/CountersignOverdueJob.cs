using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Every quarter of an hour, tells the doctor and the ward's nurses in charge of each verbal or telephone medication order,
/// and each nurse's stop, that was not countersigned in the hospital's time (<see cref="MarService.NotifyOverdueCountersignaturesAsync"/>):
/// such an order is listed and flagged, never kept silently. It also tells the nurses in charge of each urgent move to or
/// from critical care not accepted by the receiving ward within the hour (<see cref="TransferService.NotifyOverdueAcceptancesAsync"/>). Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).
/// </summary>
internal sealed class CountersignOverdueJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<CountersignOverdueJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var told = await scope.ServiceProvider.GetRequiredService<MarService>().NotifyOverdueCountersignaturesAsync(stoppingToken);
                if (told > 0)
                {
                    logger.LogInformation("Told doctors and nurses in charge of {Count} medication orders not countersigned in time", told);
                }

                var transfers = await scope.ServiceProvider.GetRequiredService<TransferService>().NotifyOverdueAcceptancesAsync(stoppingToken);
                if (transfers > 0)
                {
                    logger.LogInformation("Told nurses in charge of {Count} urgent transfers not accepted within the hour", transfers);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The check for medication orders not countersigned in time failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
