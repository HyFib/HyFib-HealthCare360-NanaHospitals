using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Every 5 minutes, reminds the ward of each NEWS2 escalation nobody has answered in its time
/// (<see cref="EarlyWarningService.RemindUnansweredAsync"/>): 30 minutes for an urgent review, 15 for an emergency, so a
/// reminder is at most 5 minutes late. Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).
/// </summary>
internal sealed class EarlyWarningReminderJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<EarlyWarningReminderJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

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
                var reminded = await scope.ServiceProvider.GetRequiredService<EarlyWarningService>().RemindUnansweredAsync(stoppingToken);
                if (reminded > 0)
                {
                    logger.LogInformation("Reminded the ward of {Count} NEWS2 escalations nobody has answered", reminded);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The reminder of unanswered NEWS2 escalations failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
