using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Every 15 minutes: marks video consultations nobody started as missed, and raises device-silence alerts for RPM
/// devices that have sent nothing for <c>rpm.deviceSilenceHours</c> (RPM-05, default 24 h). Runs only on nodes that run
/// jobs; each device alerts once per silence (the flag clears on the next reading).
/// </summary>
internal sealed class VirtualCareJobs(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<VirtualCareJobs> logger) : BackgroundService
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
                await using var scope = scopeFactory.CreateAsyncScope();
                var monitor = scope.ServiceProvider.GetRequiredService<VirtualCareMonitor>();
                var missed = await monitor.CloseMissedSessionsAsync(stoppingToken);
                var silent = await monitor.RaiseSilenceAlertsAsync(stoppingToken);
                if (missed + silent > 0)
                {
                    logger.LogInformation("Virtual care monitor: {Missed} missed consultation(s), {Silent} silent device(s)", missed, silent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Virtual care monitoring cycle failed; it will run again in {Minutes} minutes", Interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>One monitoring pass (scoped unit of work).</summary>
internal sealed class VirtualCareMonitor(
    HospitalDbContext db,
    RpmAlertService alerts,
    VirtualCareSettings settings,
    TelehealthBroadcaster broadcaster,
    TimeProvider time)
{
    private const int BatchSize = 200;

    public async Task<int> CloseMissedSessionsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var overdueBefore = now - TelehealthSession.MissedAfter;
        var overdue = await db.Set<TelehealthSession>()
            .Where(s => (s.Status == TelehealthStatus.Scheduled || s.Status == TelehealthStatus.WaitingRoom) && s.ScheduledEndAt < overdueBefore)
            .OrderBy(s => s.ScheduledEndAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        var missed = overdue.Where(s => s.MarkMissedIfOverdue(now)).ToList();
        if (missed.Count == 0)
        {
            return 0;
        }

        await db.SaveChangesAsync(ct);
        foreach (var session in missed)
        {
            await broadcaster.SessionChangedAsync(session, ct);
        }

        return missed.Count;
    }

    public async Task<int> RaiseSilenceAlertsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // The shortest allowed threshold is one hour; each device is then checked against its facility's setting.
        var candidatesBefore = now.AddHours(-1);
        var candidates = await (
                from d in db.Set<RpmDevice>()
                join e in db.Set<RpmEnrollment>() on d.EnrollmentId equals (Guid?)e.Id
                where d.IsActive && d.SilenceAlertRaisedAt == null && e.Status == RpmEnrollmentStatus.Active
                      && (d.LastSeenAt ?? d.AssignedAt) < candidatesBefore
                select new { Device = d, Enrollment = e })
            .Take(BatchSize)
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var thresholds = new Dictionary<Guid, TimeSpan>();
        var raised = new List<RpmAlert>();
        foreach (var candidate in candidates)
        {
            if (!thresholds.TryGetValue(candidate.Device.FacilityId, out var threshold))
            {
                threshold = await settings.DeviceSilenceAsync(candidate.Device.FacilityId, ct);
                thresholds[candidate.Device.FacilityId] = threshold;
            }

            if (candidate.Device.IsSilent(now, threshold))
            {
                raised.Add(await alerts.RaiseSilenceAsync(candidate.Enrollment, candidate.Device, threshold, now, ct));
            }
        }

        if (raised.Count == 0)
        {
            return 0;
        }

        await db.SaveChangesAsync(ct);
        await alerts.NotifyInAppAsync(raised, ct);
        return raised.Count;
    }
}
