using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Computes the previous two days' attendance for every facility once a night (after 01:30 IST, so late night-shift
/// out-punches and terminals that reconnect after a WiFi drop are included). Runs only on job-running nodes and
/// under a cluster-wide lock; recomputation is idempotent, so a missed or repeated run is harmless.
/// </summary>
internal sealed class AttendanceComputationJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<AttendanceComputationJob> logger) : BackgroundService
{
    private static readonly TimeOnly RunAfter = new(1, 30);
    private DateOnly? _lastRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), time);
        do
        {
            var now = time.LocalNow();
            var today = DateOnly.FromDateTime(now);
            if (_lastRun == today || TimeOnly.FromDateTime(now) < RunAfter)
            {
                continue;
            }

            try
            {
                await RunAsync(today, stoppingToken);
                _lastRun = today;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Nightly attendance computation failed; it will be retried");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunAsync(DateOnly today, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var attendance = scope.ServiceProvider.GetRequiredService<AttendanceService>();

        await using var transaction = await JobLock.TryAcquireAsync(db, JobLock.AttendanceComputation, ct);
        if (transaction is null)
        {
            logger.LogDebug("Attendance computation is running on another node");
            return;
        }

        var facilities = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.Status != EmployeeStatus.Separated)
            .Select(e => e.FacilityId)
            .Distinct()
            .ToListAsync(ct);

        var total = 0;
        foreach (var facilityId in facilities)
        {
            total += await attendance.ComputeAsync(facilityId, today.AddDays(-2), today.AddDays(-1), null, ct);
        }

        await transaction.CommitAsync(ct);
        logger.LogInformation("Computed {Count} employee-days of attendance for {Facilities} facilities", total, facilities.Count);
    }
}
