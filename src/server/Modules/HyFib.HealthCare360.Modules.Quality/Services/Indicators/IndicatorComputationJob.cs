using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Quality.Services.Indicators;

/// <summary>
/// The monthly computation of the NABH indicators HC360 computes. After 02:00 IST on each of the first
/// <see cref="RefreshDays"/> days of a month it computes the month just ended for every facility, so documentation entered
/// late in the first days of the month (a discharge, a device removal, a confirmed infection) is taken in; then the
/// month's values stand until someone recomputes them. Runs only on job-running nodes, under a cluster-wide lock; a
/// missed or repeated run is harmless.
/// </summary>
internal sealed class IndicatorComputationJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<IndicatorComputationJob> logger) : BackgroundService
{
    /// <summary>Days at the start of a month during which the month just ended is computed again each night.</summary>
    public const int RefreshDays = 10;

    internal const long LockKey = 4_247_701;
    private static readonly TimeOnly RunAfter = new(2, 0);
    private DateOnly? _lastRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30), time);
        do
        {
            var today = time.LocalToday();
            var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
            if (_lastRun == today || today.Day > RefreshDays || TimeOnly.FromDateTime(now.DateTime) < RunAfter)
            {
                continue;
            }

            try
            {
                await RunAsync(today.AddMonths(-1), stoppingToken);
                _lastRun = today;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The monthly computation of quality indicators failed; it will be retried");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunAsync(DateOnly month, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var computation = scope.ServiceProvider.GetRequiredService<IndicatorComputation>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
        {
            logger.LogDebug("The quality indicators are being computed on another node");
            return;
        }

        var facilities = await db.Set<Facility>().Where(f => f.IsActive).Select(f => f.Id).ToListAsync(ct);
        var stored = 0;
        foreach (var facilityId in facilities)
        {
            stored += (await computation.ComputeAsync(facilityId, month, null, ct)).Count(r => r.Outcome == ComputeOutcome.Stored);
        }

        await transaction.CommitAsync(ct);
        logger.LogInformation("Computed {Count} quality indicator values for {Month:MMM yyyy} in {Facilities} facilities", stored, month, facilities.Count);
    }
}
