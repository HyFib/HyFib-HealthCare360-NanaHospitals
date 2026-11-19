using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>The medication chart as <see cref="MarChartAheadJob"/> keeps it a day ahead (<see cref="MarService"/>).</summary>
internal interface IMarChartAhead
{
    /// <summary>The orders whose chart is kept a day ahead and lacks a dose in that day.</summary>
    Task<IReadOnlyList<Guid>> OrdersToChartAsync(CancellationToken ct);

    /// <summary>Charts the order's doses for the day from now, saved; the number of doses added.</summary>
    Task<int> ChartAheadAsync(Guid orderId, CancellationToken ct);
}

/// <summary>
/// Every quarter of an hour, keeps each medication order's doses on the ward's rounds a day ahead
/// (<see cref="MarService.OrdersToChartAsync"/>, <see cref="MarService.ChartAheadAsync"/>): a course the pharmacy issued at
/// once for several days is charted round by round to its end, so no dose of it is missing from the round, the due list or
/// the overdue doses. An issue or a ward order charts its first day as it is made. Runs only on nodes that run jobs
/// (<see cref="PlatformOptions.RunsJobs"/>), and with several servers only on the one that holds the job lease.
/// </summary>
internal sealed class MarChartAheadJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<MarChartAheadJob> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

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
                logger.LogWarning(ex, "Charting the medication rounds a day ahead failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One run: the doses charted.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> orderIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            orderIds = await scope.ServiceProvider.GetRequiredService<IMarChartAhead>().OrdersToChartAsync(ct);
        }

        var charted = 0;
        foreach (var orderId in orderIds)
        {
            // One scope per order: one that fails (a dose a pharmacy issue charted at the same moment) never stops the next.
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                charted += await scope.ServiceProvider.GetRequiredService<IMarChartAhead>().ChartAheadAsync(orderId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The doses of medication order {OrderId} could not be charted ahead", orderId);
            }
        }

        if (charted > 0)
        {
            logger.LogInformation("Charted {Doses} doses of {Orders} medication orders a day ahead", charted, orderIds.Count);
        }

        return charted;
    }
}
