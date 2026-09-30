using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>
/// Runs the retention clean-up (<see cref="RetentionService.RunAsync"/>) every night after 02:00 IST, and soon after an
/// administrator asks for one. Only nodes that run jobs take part, and a PostgreSQL session lock keeps two nodes from
/// cleaning up at once. The lock is held on the job's own connection rather than in a transaction, so the batches commit
/// as they go instead of in one long transaction.
/// </summary>
internal sealed class RetentionJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<RetentionJob> logger) : BackgroundService
{
    internal const string LastRunSetting = "privacy.retention.last_run";
    internal const long LockKey = 4_247_601;
    private const int RunAfterHour = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2), time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The retention clean-up failed; it will be tried again");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var now = PatientQueries.IndiaTime(time);
        var today = DateOnly.FromDateTime(now.DateTime);
        var requested = await settings.GetAsync<RetentionService.RunRequest?>(RetentionService.RunRequestedSetting, null, ct) is not null;
        var scheduled = now.Hour >= RunAfterHour && await settings.GetAsync<DateOnly?>(LastRunSetting, null, ct) != today;
        if (!requested && !scheduled)
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
            {
                return;
            }

            try
            {
                var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
                var request = requested ? await retention.TakeRunRequestAsync(ct) : null;
                var run = await retention.RunAsync(request is null ? RetentionRunTrigger.Scheduled : RetentionRunTrigger.Manual, request?.UserId, ct);
                if (scheduled)
                {
                    await settings.SetAsync(LastRunSetting, today, null, false, ct);
                }

                logger.LogInformation("Retention clean-up removed {Count} record(s)", run.TotalRemoved);
            }
            finally
            {
                await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({LockKey}) AS \"Value\"").SingleAsync(CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
