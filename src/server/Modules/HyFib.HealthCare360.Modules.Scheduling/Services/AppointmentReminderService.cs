using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// Every five minutes sends due 24-hour and 2-hour appointment reminders (OA-04) and expires stale waitlist requests.
/// Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>). Exactly-once delivery comes from the
/// reminder table (<see cref="AppointmentReminderSent"/>): a pass claims each reminder with an insert that does nothing
/// when the reminder is already there, in the same transaction as the outbox message, so a second job node waits for the
/// first and then finds the reminder taken. The appointment row is never written, so the desk's version stays good.
/// </summary>
internal sealed class AppointmentReminderService(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<AppointmentReminderService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

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
                var runner = scope.ServiceProvider.GetRequiredService<AppointmentReminderRunner>();
                var sent = await runner.RunAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("Queued {Count} appointment reminder(s)", sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Appointment reminder cycle failed; it will run again in {Minutes} minutes", Interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>One reminder pass (a scoped unit of work).</summary>
internal sealed class AppointmentReminderRunner(
    HospitalDbContext db,
    AppointmentNotifier notifier,
    WaitlistService waitlist,
    TimeProvider time,
    ILogger<AppointmentReminderRunner> logger)
{
    private const int BatchSize = 100;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var dayAhead = now + ReminderPolicy.DayBeforeLead;
        var twoHoursAhead = now + ReminderPolicy.TwoHourLead;
        var reminders = db.Set<AppointmentReminderSent>();

        var due = await db.Set<Appointment>().AsNoTracking()
            .Where(a => (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed)
                        && a.StartsAt > now && a.StartsAt <= dayAhead
                        && (!reminders.Any(r => r.AppointmentId == a.Id && r.Kind == AppointmentReminder.DayBefore)
                            || (a.StartsAt <= twoHoursAhead && !reminders.Any(r => r.AppointmentId == a.Id && r.Kind == AppointmentReminder.TwoHoursBefore))))
            .OrderBy(a => a.StartsAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        var ids = due.Select(a => a.Id).ToList();
        var already = (await reminders.AsNoTracking().Where(r => ids.Contains(r.AppointmentId)).ToListAsync(ct))
            .ToLookup(r => r.AppointmentId);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sent = 0;
        foreach (var appointment in due)
        {
            var stamps = already[appointment.Id].ToDictionary(r => r.Kind, r => (DateTimeOffset?)r.SentAt);
            var decision = ReminderPolicy.Decide(
                appointment.Status, appointment.StartsAt, appointment.BookedAt, stamps.GetValueOrDefault(AppointmentReminder.DayBefore),
                stamps.GetValueOrDefault(AppointmentReminder.TwoHoursBefore), now);

            // Each reminder this pass sends or skips is claimed first; one another pass claimed is left to it.
            var dayBefore = (decision.SendDayBefore || decision.SkipDayBefore) && await ClaimAsync(appointment.Id, AppointmentReminder.DayBefore, now, ct);
            var twoHours = (decision.SendTwoHour || decision.SkipTwoHour) && await ClaimAsync(appointment.Id, AppointmentReminder.TwoHoursBefore, now, ct);
            if ((decision.SendDayBefore && dayBefore) || (decision.SendTwoHour && twoHours))
            {
                await notifier.ReminderAsync(appointment, ct);
                sent++;
            }
        }

        await waitlist.ExpirePastAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another pass expired the same waitlist requests: nothing of this pass is kept (the reminders it claimed
            // included), and the next pass sends what is still due.
            logger.LogInformation("Reminder pass rolled back because waitlist requests changed concurrently");
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return 0;
        }

        await transaction.CommitAsync(ct);
        if (due.Count > 0 && sent == 0)
        {
            logger.LogDebug("Reminder pass found {Count} appointment(s) with nothing to send", due.Count);
        }

        return sent;
    }

    /// <summary>
    /// Records the reminder as sent, unless it already is: true when this pass recorded it. A concurrent pass inserting
    /// the same reminder waits for this transaction, then inserts nothing.
    /// </summary>
    private async Task<bool> ClaimAsync(Guid appointmentId, AppointmentReminder kind, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO scheduling.appointment_reminders (appointment_id, kind, sent_at) VALUES ({appointmentId}, {kind.ToString()}, {now}) ON CONFLICT (appointment_id, kind) DO NOTHING",
            ct) == 1;
}
