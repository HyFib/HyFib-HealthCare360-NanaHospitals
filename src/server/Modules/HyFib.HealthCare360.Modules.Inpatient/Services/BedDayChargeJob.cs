using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Daily in-patient charge job (IPB auto bed and nursing charges). It wakes at start-up and then every hour; each run
/// posts only the days missing from the ledger, so every bed day is charged exactly once, shortly after its census
/// point, and a missed midnight (server restart, power cut) is caught up automatically. It also retries consultant-visit
/// charges Billing could not accept earlier. Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).
/// </summary>
internal sealed class BedDayChargeJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<BedDayChargeJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

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
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "In-patient daily charge run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> admissionIds;
        int visitsCharged;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var charges = scope.ServiceProvider.GetRequiredService<InpatientChargeService>();
            admissionIds = await charges.StaysDueAsync(ct);
            visitsCharged = await charges.RetryVisitChargesAsync(ct);
        }

        var posted = 0;
        foreach (var admissionId in admissionIds)
        {
            // One scope per admission: a failure (for example Billing rejecting a charge) never poisons the next one.
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                posted += await scope.ServiceProvider.GetRequiredService<InpatientChargeService>().PostDueBedDaysAsync(admissionId, null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Day charges for admission {AdmissionId} could not be posted", admissionId);
            }
        }

        if (posted > 0 || visitsCharged > 0)
        {
            logger.LogInformation("Posted {DayCharges} in-patient day charges and {VisitCharges} consultant-visit charges", posted, visitsCharged);
        }
    }
}
