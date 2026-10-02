using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Every 15 minutes on nodes that run jobs: escalates complaints past their deadline (FB-03), and once a day, in the
/// daytime window campaigns also use (10:00–21:00 IST), reminds patients of follow-ups falling due (CRM-02).
/// </summary>
internal sealed class EngagementJobs(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<EngagementJobs> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private DateOnly? _remindedOn;

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
                var monitor = scope.ServiceProvider.GetRequiredService<EngagementMonitor>();
                var escalated = await monitor.EscalateOverdueComplaintsAsync(stoppingToken);
                var local = EngagementClock.ToLocal(time.GetUtcNow());
                var today = DateOnly.FromDateTime(local);
                var reminded = 0;
                if (_remindedOn != today && CampaignSendWindow.IsOpen(local))
                {
                    reminded = await monitor.SendFollowUpRemindersAsync(stoppingToken);
                    _remindedOn = today;
                }

                if (escalated + reminded > 0)
                {
                    logger.LogInformation("Engagement jobs: {Escalated} complaint(s) escalated, {Reminded} patient(s) reminded of a follow-up", escalated, reminded);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Engagement jobs failed; they will run again in {Minutes} minutes", Interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// One pass of the Engagement jobs, a scoped unit of work. Each pass holds a PostgreSQL advisory lock for its
/// transaction, so two job nodes never send the same reminder or escalation twice.
/// </summary>
internal sealed class EngagementMonitor(
    HospitalDbContext db,
    FollowUpReminders reminders,
    ComplaintService complaints,
    EngagementSettings settings,
    TimeProvider time,
    ILogger<EngagementMonitor> logger)
{
    private const long ReminderLockKey = 4_247_502;
    private const long EscalationLockKey = 4_247_503;
    private const int EscalationBatchSize = 100;

    /// <summary>
    /// Sends the "follow-up due" SMS for open "come back" tasks falling due within their facility's reminder days, one
    /// message per patient. A patient who cannot get it (no consent or no mobile number) is checked again the next day,
    /// as the consent may be given meanwhile. Returns the patients reminded.
    /// </summary>
    public async Task<int> SendFollowUpRemindersAsync(CancellationToken ct)
    {
        var today = EngagementClock.LocalDate(time.GetUtcNow());
        var horizon = today.AddDays(EngagementSettings.MaxReminderDays);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await TryLockAsync(ReminderLockKey, ct))
        {
            return 0;
        }

        var candidates = await db.Set<FollowUpTask>()
            .Where(t => (t.Status == FollowUpStatus.Open || t.Status == FollowUpStatus.InProgress) && t.ReminderSentAt == null
                        && t.DueDate >= today && t.DueDate <= horizon
                        && (t.Type == FollowUpType.PostConsultation || t.Type == FollowUpType.Vaccination
                            || t.Type == FollowUpType.ChronicReview || t.Type == FollowUpType.HealthCheckupDue))
            .ToListAsync(ct);
        var leadDays = new Dictionary<Guid, int>();
        var reminded = 0;
        var unreachable = 0;
        foreach (var patient in candidates.GroupBy(t => (t.PatientId, t.FacilityId)))
        {
            if (!leadDays.TryGetValue(patient.Key.FacilityId, out var days))
            {
                leadDays[patient.Key.FacilityId] = days = await settings.ReminderDaysAsync(patient.Key.FacilityId, ct);
            }

            var due = patient.Where(t => FollowUpAutomation.IsReminderDue(t, today, days)).ToList();
            if (due.Count == 0)
            {
                continue;
            }

            if ((await reminders.QueueAsync(due, ct)).IsSuccess)
            {
                reminded++;
            }
            else
            {
                unreachable++;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (unreachable > 0)
        {
            logger.LogInformation("{Count} patient(s) with a follow-up due were not sent a reminder (no SMS consent or mobile number)", unreachable);
        }

        return reminded;
    }

    /// <summary>Escalates complaints still unresolved after their deadline, once each, then alerts the escalation recipients and the owner.</summary>
    public async Task<int> EscalateOverdueComplaintsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<Complaint> escalated;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await TryLockAsync(EscalationLockKey, ct))
            {
                return 0;
            }

            var overdue = await db.Set<Complaint>()
                .Where(c => (c.Status == ComplaintStatus.Open || c.Status == ComplaintStatus.InProgress) && c.DueAt < now && c.EscalatedAt == null)
                .OrderBy(c => c.DueAt)
                .Take(EscalationBatchSize)
                .ToListAsync(ct);
            escalated = overdue.Where(c => c.Escalate(now)).ToList();
            if (escalated.Count == 0)
            {
                return 0;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Alerts follow the commit: a failure here loses an alert, but never sends one for an escalation that was not saved.
        foreach (var complaint in escalated)
        {
            await complaints.NotifyEscalatedAsync(complaint, ct);
        }

        return escalated.Count;
    }

    private async Task<bool> TryLockAsync(long key, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(ct);
}
