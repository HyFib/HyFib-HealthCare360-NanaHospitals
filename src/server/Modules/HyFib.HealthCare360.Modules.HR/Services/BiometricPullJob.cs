using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Every minute: reads each active terminal set to be read by HC360 whose interval has passed, and tells HR once
/// about terminals that have gone quiet (INT-BIO). Runs only on job-running nodes; each terminal is read under its own
/// cluster-wide lock, in its own scope, so one unreachable terminal does not hold up the others for long.
/// </summary>
internal sealed class BiometricPullJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<BiometricPullJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reading attendance terminals failed; it is tried again in a minute");
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        List<Guid> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var now = time.GetUtcNow();
            var terminals = await db.Set<BiometricDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(d => d.IsActive && d.Connection == BiometricConnection.Pull)
                .Select(d => new { d.Id, d.LastPullAt, d.PullEveryMinutes })
                .ToListAsync(ct);
            due = terminals
                .Where(d => d.LastPullAt is null || now - d.LastPullAt.Value >= TimeSpan.FromMinutes(Math.Clamp(d.PullEveryMinutes, 1, BiometricDevice.MaxPullMinutes)))
                .OrderBy(d => d.LastPullAt)
                .Select(d => d.Id)
                .ToList();
        }

        foreach (var id in due)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<BiometricPullService>().PullAsync(id, ct);
            if (result.Success && result.Punches is { Accepted: > 0 } punches)
            {
                logger.LogInformation("Read {Accepted} new punches from attendance terminal {DeviceId}", punches.Accepted, id);
            }
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var quiet = await scope.ServiceProvider.GetRequiredService<BiometricPullService>().ReportQuietTerminalsAsync(ct);
            if (quiet > 0)
            {
                logger.LogWarning("{Count} attendance terminal(s) have gone quiet; HR has been told", quiet);
            }
        }
    }
}
