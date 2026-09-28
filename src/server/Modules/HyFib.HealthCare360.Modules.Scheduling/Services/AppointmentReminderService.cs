using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// Every five minutes sends due 24-hour and 2-hour appointment reminders (OA-04) and expires stale waitlist requests.
/// Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>). Exactly-once delivery comes from the
/// reminder flags: each flag is saved in the same transaction as the outbox message, and the appointment's
/// concurrency version makes a second worker's copy of the same batch fail instead of sending twice.
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

        var due = await db.Set<Appointment>()
            .Where(a => (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed)
                        && a.StartsAt > now && a.StartsAt <= dayAhead
                        && (a.Reminder24hSentAt == null || (a.Reminder2hSentAt == null && a.StartsAt <= twoHoursAhead)))
            .OrderBy(a => a.StartsAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var appointment in due)
        {
            var decision = ReminderPolicy.Decide(appointment, now);
            if (decision.SendsMessage)
            {
                await notifier.ReminderAsync(appointment, ct);
                sent++;
            }

            if (decision.SendDayBefore || decision.SkipDayBefore)
            {
                appointment.MarkReminderSent(AppointmentReminder.DayBefore, now);
            }

            if (decision.SendTwoHour || decision.SkipTwoHour)
            {
                appointment.MarkReminderSent(AppointmentReminder.TwoHoursBefore, now);
            }
        }

        var expired = await waitlist.ExpirePastAsync(ct);
        if (due.Count == 0 && expired == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker (or a desk edit) touched one of these appointments: nothing was sent; the next pass retries.
            logger.LogInformation("Reminder batch skipped because appointments changed concurrently");
            db.ChangeTracker.Clear();
            return 0;
        }

        return sent;
    }
}
