using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Watches the active restraints of every facility: one alert for each nurse's check that is overdue (past
/// <see cref="RestraintEpisode.CheckIntervalMinutes"/> since the last check, or since the order), and one for an order that
/// passed <see cref="RestraintEpisode.OrderedUntil"/> with no renewal or release. The nurses in charge are told, by UHID,
/// ward and bed only, and each alert is audited at Warning. An overdue period is alerted once (the due time it was raised for
/// is kept); a check that moves the due time on, or a renewal that lapses in its turn, raises a new alert.
/// </summary>
public sealed class RestraintAlertService(HospitalDbContext db, INotificationService notifications, IAuditLogger audit, TimeProvider time)
{
    private const string Category = "inpatient";

    /// <summary>How many episodes are read at a time; every active episode is looked at, page after page.</summary>
    internal const int PageSize = 500;

    /// <summary>
    /// Raises the alerts due now, for the patients still in a bed (open stays), in every facility; returns how many it raised.
    /// Every active episode is looked at: their ids are read first, then the episodes a page at a time, so none is left out
    /// however many there are, and one released while the run goes on is not skipped by the paging.
    /// </summary>
    public async Task<int> RaiseAlertsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var ignore = new[] { HospitalDbContext.FacilityFilter };
        var ids = await (
                from e in db.Set<RestraintEpisode>().IgnoreQueryFilters(ignore)
                where e.Status == RestraintStatus.Active
                join a in db.Set<Admission>().IgnoreQueryFilters(ignore) on e.AdmissionId equals a.Id
                where a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated
                orderby e.OrderedAt, e.Id
                select e.Id)
            .ToListAsync(ct);
        var raised = 0;
        foreach (var page in ids.Chunk(PageSize))
        {
            var episodes = await db.Set<RestraintEpisode>().IgnoreQueryFilters(ignore)
                .Where(e => page.Contains(e.Id) && e.Status == RestraintStatus.Active)
                .OrderBy(e => e.OrderedAt).ThenBy(e => e.Id)
                .ToListAsync(ct);
            raised += await RaiseAsync(episodes, now, ct);
        }

        return raised;
    }

    private async Task<int> RaiseAsync(IReadOnlyList<RestraintEpisode> active, DateTimeOffset now, CancellationToken ct)
    {
        var raised = 0;
        foreach (var episode in active)
        {
            if (episode.IsCheckAlertDue(now))
            {
                var minutes = (int)(now - episode.NextCheckDue).TotalMinutes;
                await TellAsync(episode, "Restraint check overdue",
                    $"The {episode.CheckIntervalMinutes}-minute check of the restrained patient is {minutes} min overdue: check circulation, skin, fluids and toileting.",
                    "restraint.check_overdue", $"A restraint check was overdue by {minutes} minutes; the nurses in charge were told", tellDoctors: false, ct);
                episode.MarkCheckAlerted();
                await SaveMarkerAsync(episode, e => e.IsCheckAlertDue(now), e => e.MarkCheckAlerted(), ct);
                raised++;
            }

            if (episode.IsLapseAlertDue(now))
            {
                await TellAsync(episode, "Restraint order expired",
                    $"The order ran out at {IndiaClock.LocalTime(episode.OrderedUntil):HH\\:mm} and the patient is still restrained: a doctor renews it after seeing the patient, or releases them.",
                    "restraint.order_lapsed", "A restraint order lapsed with the patient still restrained; the nurses in charge, the ordering doctor and the consultant were told",
                    tellDoctors: true, ct);
                episode.MarkLapseAlerted();
                await SaveMarkerAsync(episode, e => e.IsLapseAlertDue(now), e => e.MarkLapseAlerted(), ct);
                raised++;
            }
        }

        return raised;
    }

    /// <summary>
    /// Saves one episode's marker on its own, so a nurse's check or a doctor's renewal or release of another episode never loses it.
    /// On a conflict the episode is read again: if its alert is still due the marker is set again and saved once more, otherwise
    /// (checked, renewed, released meanwhile) there is nothing to mark.
    /// </summary>
    private async Task SaveMarkerAsync(RestraintEpisode episode, Func<RestraintEpisode, bool> stillDue, Action<RestraintEpisode> mark, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                await db.Entry(episode).ReloadAsync(ct);
                if (!stillDue(episode))
                {
                    return;
                }

                mark(episode);
            }
        }

        // Gave up after repeated conflicts: leave nothing modified in the shared context, or it would fail every later save of
        // the run. The alert is still due, so the next run raises it again (rare, and a repeat is safer than a lost alert).
        db.Entry(episode).State = EntityState.Detached;
    }

    private async Task TellAsync(
        RestraintEpisode episode, string title, string action, string auditAction, string summary, bool tellDoctors, CancellationToken ct)
    {
        var ignore = new[] { HospitalDbContext.FacilityFilter };
        var uhid = await db.Set<Patient>().IgnoreQueryFilters(ignore).Where(p => p.Id == episode.PatientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);
        var place = await (
                from a in db.Set<Admission>().IgnoreQueryFilters(ignore)
                where a.Id == episode.AdmissionId
                join w in db.Set<Ward>().IgnoreQueryFilters(ignore) on a.WardId equals w.Id into wards
                from w in wards.DefaultIfEmpty()
                join b in db.Set<Bed>().IgnoreQueryFilters(ignore) on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                select new { Ward = w != null ? w.Name : null, Bed = b != null ? b.Label : null, a.ConsultantPractitionerId })
            .FirstOrDefaultAsync(ct);
        var body = $"{uhid}, {place?.Ward ?? "ward"} bed {place?.Bed ?? "-"}: {action}";
        var link = $"/inpatient/admissions/{episode.AdmissionId}";
        if (tellDoctors)
        {
            // Only a doctor renews or releases: the ordering doctor and the stay's consultant are told with the nurses in charge.
            var orderedBy = episode.OrderedByUserId;
            var consultant = place?.ConsultantPractitionerId;
            var doctors = await db.Set<User>().AsNoTracking().IgnoreQueryFilters(ignore)
                .Where(u => u.IsActive && u.Type == UserType.Staff
                            && ((orderedBy != null && u.Id == orderedBy) || (consultant != null && u.PractitionerId == consultant)))
                .Select(u => u.Id)
                .ToListAsync(ct);
            await notifications.NotifyUsersAsync(doctors, title, body, link, Category, "critical", ct);
        }

        await notifications.NotifyPermissionHoldersAsync(InpatientPermissions.NursingInCharge, episode.FacilityId, title, body, link, Category, "critical", ct);
        // The job runs for every facility with no one signed in: the entry is filed under the episode's own facility.
        await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, auditAction, summary)
        {
            FacilityId = episode.FacilityId,
            PatientId = episode.PatientId,
            EntityType = nameof(RestraintEpisode),
            EntityId = episode.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { admissionId = episode.AdmissionId, type = episode.Type.ToString(), until = episode.OrderedUntil },
        }, ct);
    }
}

/// <summary>Every 5 minutes runs <see cref="RestraintAlertService"/>. Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).</summary>
internal sealed class RestraintAlertJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<RestraintAlertJob> logger)
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
                var raised = await scope.ServiceProvider.GetRequiredService<RestraintAlertService>().RaiseAlertsAsync(stoppingToken);
                if (raised > 0)
                {
                    logger.LogInformation("Raised {Count} restraint alerts (overdue checks, lapsed orders)", raised);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The check for overdue restraint checks and lapsed orders failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
