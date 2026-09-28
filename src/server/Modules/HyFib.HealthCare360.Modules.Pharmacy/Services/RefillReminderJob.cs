using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// Chronic-medication refill reminders (PH-05). Once a day after 09:00 IST, patients whose last supply of a
/// refillable prescription runs out within <see cref="LeadDays"/> days get an SMS/WhatsApp reminder — at most one
/// per dispensed supply, and only while their SMS/WhatsApp consent is active. Runs only on job-running nodes.
/// </summary>
internal sealed class RefillReminderJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<RefillReminderJob> logger) : BackgroundService
{
    internal const int LeadDays = 3;
    private const int RunAfterHour = 9;
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
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Refill reminders failed; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The day the last supply runs out: last dispense date plus the longest course on the prescription.</summary>
    internal static DateOnly SupplyEndsOn(DateTimeOffset lastDispensedAt, int longestCourseDays) =>
        HospitalClock.ToLocalDate(lastDispensedAt).AddDays(Math.Max(longestCourseDays, 1));

    private async Task RunIfDueAsync(CancellationToken ct)
    {
        var now = HospitalClock.LocalNow(time);
        var today = DateOnly.FromDateTime(now.DateTime);
        if (now.Hour < RunAfterHour || _lastRun == today)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var originals = await db.Set<PharmacyQueueEntry>().Include(e => e.Lines)
            .Where(e => e.RefillOfEntryId == null && !e.IsInpatient && !e.HasControlledDrugs && e.Status == QueueEntryStatus.Dispensed
                        && e.RefillsAuthorized > e.RefillsUsed && (e.RefillValidUntil == null || e.RefillValidUntil >= today))
            .ToListAsync(ct);
        var originalIds = originals.Select(e => e.Id).ToList();
        var refillDispenses = await db.Set<PharmacyQueueEntry>().AsNoTracking()
            .Where(e => e.RefillOfEntryId != null && originalIds.Contains(e.RefillOfEntryId.Value) && e.LastDispensedAt != null)
            .GroupBy(e => e.RefillOfEntryId!.Value)
            .Select(g => new { OriginalId = g.Key, Last = g.Max(e => e.LastDispensedAt) })
            .ToDictionaryAsync(x => x.OriginalId, x => x.Last, ct);

        var due = originals
            .Select(e =>
            {
                var last = refillDispenses.GetValueOrDefault(e.Id) is { } refilled && refilled > e.LastDispensedAt ? refilled : e.LastDispensedAt;
                return (Entry: e, Last: last);
            })
            .Where(x => x.Last is not null
                        && (x.Entry.LastRefillReminderAt is null || x.Entry.LastRefillReminderAt < x.Last)
                        && SupplyEndsOn(x.Last.Value, x.Entry.Lines.Select(l => l.DurationDays).DefaultIfEmpty(1).Max()).AddDays(-LeadDays) <= today)
            .ToList();
        if (due.Count > 0)
        {
            var patientIds = due.Select(x => x.Entry.PatientId).Distinct().ToList();
            var patients = await db.Set<Patient>().AsNoTracking()
                .Where(p => patientIds.Contains(p.Id) && p.IsActive && !p.IsDeceased && p.Phone != null
                            && db.Set<PatientConsent>().Any(c => c.PatientId == p.Id && c.Type == ConsentType.SmsWhatsAppUpdates && c.Granted && c.RevokedAt == null))
                .Select(p => new { p.Id, p.FullName, p.Phone, p.Email })
                .ToDictionaryAsync(p => p.Id, ct);

            foreach (var (entry, last) in due)
            {
                entry.LastRefillReminderAt = time.GetUtcNow();
                if (!patients.TryGetValue(entry.PatientId, out var patient))
                {
                    continue;
                }

                notifications.Queue(RefillService.ReminderTemplate,
                    new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
                    new Dictionary<string, string>
                    {
                        ["medicines"] = string.Join(", ", entry.Lines.Select(l => l.DrugName).Take(3)),
                        ["reference"] = entry.PrescriptionNumber,
                        ["date"] = SupplyEndsOn(last!.Value, entry.Lines.Select(l => l.DurationDays).DefaultIfEmpty(1).Max()).ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
                    },
                    [NotificationChannel.Sms, NotificationChannel.WhatsApp],
                    TimeSpan.FromDays(1),
                    entry.FacilityId);
            }

            await db.SaveChangesAsync(ct);
        }

        _lastRun = today;
        logger.LogInformation("Refill reminders queued for {Count} prescription(s)", due.Count);
    }
}
