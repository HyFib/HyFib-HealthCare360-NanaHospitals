using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>
/// Keeps the number series' high-water mark (<see cref="NumberSeriesHighWaterMark"/>) on the node that runs the jobs: it
/// reads every series' counter as it starts and every minute after, writes the mark when one changed, and once more as the
/// server stops, so that a mark read after a restore misses at most the last minute's numbers (which the margin covers,
/// <see cref="NumberSeriesHighWater.Margin"/>). It only reads the counters: a write that fails is logged, once until it
/// works again, and tried again a minute later, while numbers are issued as before.
/// </summary>
internal sealed class NumberSeriesHighWaterJob(
    IServiceScopeFactory scopes,
    NumberSeriesHighWaterMark mark,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<NumberSeriesHighWaterJob> logger) : BackgroundService
{
    /// <summary>How often the counters are read.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await RecordAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The numbers issued since the last minute's mark, recorded as the server stops.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (options.Value.RunsJobs)
        {
            await RecordAsync(cancellationToken);
        }
    }

    /// <summary>Reads every counter and records them; true when the mark was written. Never throws but for cancellation.</summary>
    internal async Task<bool> RecordAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

            // A new database has no facility until its first start has made one, and nothing was issued in it yet.
            if (await NumberSeriesHighWater.DatabaseIdAsync(db, cancellationToken) is not { } database)
            {
                return false;
            }

            var written = mark.Record(database, await NumberSeriesHighWater.ReadCountersAsync(db, cancellationToken), time.GetUtcNow());
            if (_failing)
            {
                logger.LogInformation("The number series' high-water mark is written to {Path} again", mark.Path);
                _failing = false;
            }

            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!_failing)
            {
                logger.LogWarning(ex,
                    "The number series' high-water mark could not be written to {Path}; numbers are issued as before, and it is tried again in a minute. Until it is, a restore cannot move the series past the numbers issued since it was last written",
                    mark.Path);
            }

            _failing = true;
            return false;
        }
    }
}
