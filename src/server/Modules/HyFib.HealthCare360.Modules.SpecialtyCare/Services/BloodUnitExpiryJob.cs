using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Daily blood stock housekeeping (BB-05): runs once at start-up (to catch up after downtime) and then every day
/// shortly after midnight IST, marking expired units and alerting blood bank staff. Skipped on API-only nodes.
/// </summary>
internal sealed class BloodUnitExpiryJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<BloodUnitExpiryJob> logger) : BackgroundService
{
    /// <summary>Local time of the daily run (a few minutes after midnight so "today" has rolled over everywhere).</summary>
    internal static readonly TimeOnly RunAt = new(0, 5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        await RunOnceAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DelayUntilNextRun(time.GetUtcNow()), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>Time until the next <see cref="RunAt"/> in hospital-local time (always positive, at most 24 hours).</summary>
    internal static TimeSpan DelayUntilNextRun(DateTimeOffset utcNow)
    {
        var local = SpecialtyClock.ToLocal(utcNow);
        var today = DateOnly.FromDateTime(local.DateTime);
        var next = SpecialtyClock.StartOfDayUtc(today) + RunAt.ToTimeSpan();
        if (next <= utcNow)
        {
            next = SpecialtyClock.StartOfDayUtc(today.AddDays(1)) + RunAt.ToTimeSpan();
        }

        return next - utcNow;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var expired = await scope.ServiceProvider.GetRequiredService<BloodInventoryService>().MarkExpiredUnitsAsync(ct);
            logger.LogInformation("Blood stock expiry check marked {Count} unit(s) as expired", expired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Blood stock expiry check failed; it will run again at the next schedule");
        }
    }
}
