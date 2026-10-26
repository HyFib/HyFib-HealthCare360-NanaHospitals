using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Hourly, erases the records received from other providers through ABDM whose consent has passed its erase date
/// (<see cref="AbdmHealthInformationService.EraseExpiredAsync"/>); they are no longer shown from that moment, and gone
/// within the hour. Runs once at start-up to catch up after downtime. Skipped on API-only nodes.
/// </summary>
internal sealed class AbdmReceivedRecordEraser(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<AbdmReceivedRecordEraser> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var erased = await scope.ServiceProvider.GetRequiredService<AbdmHealthInformationService>().EraseExpiredAsync(stoppingToken);
                if (erased > 0)
                {
                    logger.LogInformation("Erased {Count} record(s) received through ABDM past their consent's erase date", erased);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Erasing records received through ABDM failed; it runs again within the hour");
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
