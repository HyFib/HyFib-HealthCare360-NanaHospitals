using System.Collections.Concurrent;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>A study that may be linked with PACS, as the linking job sorts it.</summary>
internal sealed record PacsLinkCandidate(Guid Id, ImagingStudyStatus Status, DateTimeOffset OrderedAt);

/// <summary>Which studies a linking run looks for (pure; unit-tested).</summary>
internal static class PacsLinkSchedule
{
    /// <summary>
    /// Up to <paramref name="batch"/> studies, skipping those looked for less than <paramref name="recheckAfter"/> ago:
    /// finished scans first (their images are most likely in PACS), then scans under way, arrivals, appointments that have
    /// started and unscheduled orders; within each, studies never looked for, then those looked for longest ago, then the
    /// oldest order. So every study gets its turn, however many wait.
    /// </summary>
    public static IReadOnlyList<Guid> Pick(
        IEnumerable<PacsLinkCandidate> candidates, Func<Guid, DateTimeOffset?> lastChecked, DateTimeOffset now, int batch, TimeSpan recheckAfter) =>
        candidates
            .Select(c => (Candidate: c, Checked: lastChecked(c.Id)))
            .Where(c => c.Checked is not { } at || now - at >= recheckAfter)
            .OrderBy(c => Rank(c.Candidate.Status))
            .ThenBy(c => c.Checked ?? DateTimeOffset.MinValue)
            .ThenBy(c => c.Candidate.OrderedAt)
            .Take(Math.Max(0, batch))
            .Select(c => c.Candidate.Id)
            .ToList();

    public static int Rank(ImagingStudyStatus status) => status switch
    {
        ImagingStudyStatus.Acquired => 0,
        ImagingStudyStatus.InProgress => 1,
        ImagingStudyStatus.Arrived => 2,
        ImagingStudyStatus.Scheduled => 3,
        _ => 4,
    };
}

/// <summary>
/// What the linking job remembers between runs, in memory: when each study was last looked for in PACS, and which
/// patient-ID problems the department has already been told about. A restart forgets it, which only means one more
/// look and one more alert.
/// </summary>
internal sealed class PacsLinkAttempts
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _checked = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _reported = new();

    public DateTimeOffset? LastChecked(Guid studyId) => _checked.TryGetValue(studyId, out var at) ? at : null;

    public void Checked(IEnumerable<Guid> studyIds, DateTimeOffset at)
    {
        foreach (var id in studyIds)
        {
            _checked[id] = at;
        }
    }

    /// <summary>True the first time a mismatch is reported for the study.</summary>
    public bool FirstReport(Guid studyId, DateTimeOffset at) => _reported.TryAdd(studyId, at);

    /// <summary>Forgets studies that have left the linking window, so the memory stays small.</summary>
    public void Prune(DateTimeOffset before)
    {
        foreach (var (id, at) in _checked)
        {
            if (at < before)
            {
                _checked.TryRemove(id, out _);
            }
        }

        foreach (var (id, at) in _reported)
        {
            if (at < before)
            {
                _reported.TryRemove(id, out _);
            }
        }
    }
}

/// <summary>
/// Links recently ordered imaging studies with their images in PACS, so nobody has to press "Link images from PACS"
/// (IMG-02, INT-PACS reconciliation by accession number). Every <see cref="Interval"/>, for each active facility with
/// <c>radiology.pacs.autoLink</c> on (the default), it looks for up to <see cref="ImagingService.ReconcileBatchSize"/>
/// unlinked studies ordered in the last <see cref="ImagingService.ReconcileWindow"/>, with the facility's own PACS
/// settings, and links those found. A study is looked for again only after <see cref="RecheckAfter"/>. When PACS cannot be
/// used the facility's run stops at the first study. A study PACS holds under another patient ID, without a patient ID,
/// or as several studies is not linked (<see cref="PacsStudyMatch"/>), and the radiology staff are told once. Runs
/// only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).
/// </summary>
internal sealed class PacsLinkJob(
    IServiceScopeFactory scopes,
    PacsLinkAttempts attempts,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<PacsLinkJob> logger) : BackgroundService
{
    public const string AutoLinkSetting = "radiology.pacs.autoLink";

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Most candidates read per facility and run, before choosing the batch.</summary>
    private const int CandidateLimit = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            // The first run waits one interval, so start-up (migrations, seeding) is not slowed by PACS searches.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        List<Guid> facilities;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            facilities = [];
            foreach (var facilityId in await db.Set<Facility>().Where(f => f.IsActive).Select(f => f.Id).ToListAsync(ct))
            {
                if (await settings.GetAsync<bool?>(AutoLinkSetting, facilityId, ct) ?? true)
                {
                    facilities.Add(facilityId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Linking imaging studies with PACS could not start; it will run again in {Minutes} minutes", Interval.TotalMinutes);
            return;
        }

        foreach (var facilityId in facilities)
        {
            try
            {
                await LinkFacilityAsync(facilityId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Linking imaging studies with PACS failed for facility {FacilityId}; it will run again", facilityId);
            }
        }

        attempts.Prune(time.GetUtcNow() - ImagingService.ReconcileWindow - TimeSpan.FromDays(1));
    }

    private async Task LinkFacilityAsync(Guid facilityId, CancellationToken ct)
    {
        // One scope per facility: a fresh unit of work, and no facility filter (there is no signed-in user), so the
        // imaging service filters by the facility itself.
        await using var scope = scopes.CreateAsyncScope();
        var imaging = scope.ServiceProvider.GetRequiredService<ImagingService>();
        var now = time.GetUtcNow();
        var candidates = await imaging.PacsLinkCandidates(facilityId, now)
            .OrderBy(s => s.Status == ImagingStudyStatus.Acquired ? 0
                : s.Status == ImagingStudyStatus.InProgress ? 1
                : s.Status == ImagingStudyStatus.Arrived ? 2
                : s.Status == ImagingStudyStatus.Scheduled ? 3 : 4)
            .ThenBy(s => s.OrderedAt)
            .Take(CandidateLimit)
            .Select(s => new PacsLinkCandidate(s.Id, s.Status, s.OrderedAt))
            .ToListAsync(ct);
        var batch = PacsLinkSchedule.Pick(candidates, attempts.LastChecked, now, ImagingService.ReconcileBatchSize, RecheckAfter);
        if (batch.Count == 0)
        {
            return;
        }

        var outcome = await imaging.LinkBatchAsync(facilityId, batch, ct);
        attempts.Checked(outcome.CheckedIds, now);
        if (outcome.Linked > 0)
        {
            logger.LogInformation("Linked {Linked} of {Checked} imaging studies with their images in PACS", outcome.Linked, outcome.Checked);
        }

        if (outcome.Unavailable is { } reason)
        {
            // Expected while PACS is not set up for the facility; the integration log shows real outages.
            logger.LogDebug("Linking with PACS stopped for facility {FacilityId}: {Reason}", facilityId, reason);
        }

        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        foreach (var mismatch in outcome.Mismatches.Where(m => attempts.FirstReport(m.StudyId, now)))
        {
            await notifications.NotifyPermissionHoldersAsync(
                DiagnosticsPermissions.RadiologyWorklist,
                facilityId,
                $"Images in PACS not linked: {mismatch.StudyName}",
                $"{mismatch.Reason} The images of {mismatch.AccessionNumber} were not linked.",
                $"/radiology/studies/{mismatch.StudyId}",
                "diagnostics",
                "warning",
                ct);
        }
    }
}
