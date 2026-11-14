using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>The morning reminder of documents waiting too long for an IRN, to holders of the e-invoice register.</summary>
/// <param name="Count">How many documents it names.</param>
/// <param name="Severity"><c>critical</c> while one of them can still reach the portal within <see cref="EInvoiceReminders.DueSoonDays"/> days, else <c>warning</c>.</param>
public sealed record EInvoiceReminder(int Count, string Title, string Body, string Severity);

/// <summary>
/// The daily reminder of B2B documents waiting for an IRN (GST rule 48(4)): those of the register pending longer than the
/// facility allows (<see cref="EInvoiceService.AlertAfterDaysSetting"/>), with what makes them urgent, the portal's 30 days
/// from a document's date (<see cref="EInvoiceRow.ReportBy"/>), after which the portal refuses it from a large taxpayer. It is
/// critical while a document can still be reported within <see cref="DueSoonDays"/> days, and counts those already past the
/// portal's days, which only the hospital's GST adviser can settle.
/// </summary>
public static class EInvoiceReminders
{
    /// <summary>A document due on the portal within this many days makes the reminder critical.</summary>
    public const int DueSoonDays = 7;

    /// <summary>The reminder of <paramref name="rows"/> on <paramref name="today"/>; null when no document has waited more than <paramref name="afterDays"/> days.</summary>
    public static EInvoiceReminder? Of(IEnumerable<EInvoiceRow> rows, DateOnly today, int afterDays)
    {
        var waiting = rows
            .Where(r => r.State == EInvoiceState.Pending && today.DayNumber - r.Date.DayNumber > afterDays)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.Number, StringComparer.Ordinal)
            .ToList();
        if (waiting.Count == 0)
        {
            return null;
        }

        var oldest = waiting[0];
        var dueSoon = waiting.Where(r => today <= r.ReportBy && r.ReportBy.DayNumber - today.DayNumber < DueSoonDays).OrderBy(r => r.ReportBy).ToList();
        var past = waiting.Count(r => today > r.ReportBy);
        var one = waiting.Count == 1;
        var body = one
            ? $"One B2B document has waited more than {Days(afterDays)} for an IRN: {oldest.Number} of {Date(oldest.Date)}."
            : $"{waiting.Count} B2B documents have waited more than {Days(afterDays)} for an IRN; the oldest is {oldest.Number} of {Date(oldest.Date)}.";
        if (dueSoon.Count > 0)
        {
            body += one
                ? $" It is due on the portal by {Date(dueSoon[0].ReportBy)}."
                : $" {(dueSoon.Count == 1 ? "One is" : $"{dueSoon.Count} are")} due on the portal within {DueSoonDays} days, the first, {dueSoon[0].Number}, by {Date(dueSoon[0].ReportBy)}.";
        }

        if (past > 0)
        {
            var days = EInvoiceService.ReportWithinDays;
            body += one
                ? $" It is past the portal's {days} days: settle it with the hospital's GST adviser."
                : $" {(past == 1 ? "One is" : $"{past} are")} past the portal's {days} days: settle {(past == 1 ? "it" : "them")} with the hospital's GST adviser.";
        }

        return new EInvoiceReminder(
            waiting.Count, $"{waiting.Count} e-invoice{(waiting.Count == 1 ? string.Empty : "s")} waiting for an IRN", body, dueSoon.Count > 0 ? "critical" : "warning");
    }

    private static string Days(int count) => count == 1 ? "a day" : $"{count} days";

    private static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Outbox payload: the reminder of documents waiting for an IRN, for one facility and day.</summary>
public sealed record EInvoiceReminderMessage(Guid FacilityId, DateOnly Date);

/// <summary>Delivers <c>billing.einvoice.reminder</c> (<see cref="EInvoiceService.RemindAsync"/>), reading the register as it is then.</summary>
internal sealed class EInvoiceReminderHandler(EInvoiceService einvoices) : IOutboxHandler
{
    public const string MessageType = "billing.einvoice.reminder";

    public string Type => MessageType;

    /// <summary>The reminder is in the app, read from the register: it does not wait for the portal or the internet.</summary>
    public OutboxLane Lane => OutboxLane.Local;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var reminder = message.Read<EInvoiceReminderMessage>();
        await einvoices.RemindAsync(reminder.FacilityId, reminder.Date, cancellationToken);
    }
}

/// <summary>
/// Each morning from <see cref="RunAfter"/> (the hospital's time), queues the reminder of documents waiting too long for
/// an IRN for every facility that e-invoices: one outbox message per facility and day, written under a cluster-wide lock
/// and only when none was written that day, so a second server or a restart never sends it twice, and a server that was off
/// in the morning sends it when it starts. Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).
/// </summary>
internal sealed class EInvoiceReminderJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<EInvoiceReminderJob> logger) : BackgroundService
{
    internal const long LockKey = 4_247_201;

    /// <summary>When the billing desk is in.</summary>
    internal static readonly TimeOnly RunAfter = new(9, 0);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

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
                var now = BillingTime.LocalNow(time);
                if (TimeOnly.FromDateTime(now.DateTime) >= RunAfter && await QueueAsync(DateOnly.FromDateTime(now.DateTime), stoppingToken) is > 0 and var queued)
                {
                    logger.LogInformation("Queued the reminder of e-invoices waiting for an IRN for {Count} facilities", queued);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not queue the reminder of e-invoices waiting for an IRN");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Queues <paramref name="today"/>'s reminder for every active facility that e-invoices and has none queued that day;
    /// how many were queued (none when another server holds the lock).
    /// </summary>
    internal async Task<int> QueueAsync(DateOnly today, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
        {
            return 0;
        }

        var dayStarted = BillingTime.StartOfDay(today);
        var queuedToday = (await db.Set<OutboxMessage>()
                .Where(m => m.Type == EInvoiceReminderHandler.MessageType && m.CreatedAt >= dayStarted && m.FacilityId != null)
                .Select(m => m.FacilityId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var queued = 0;
        foreach (var facilityId in await db.Set<Facility>().Where(f => f.IsActive).Select(f => f.Id).ToListAsync(ct))
        {
            if (queuedToday.Contains(facilityId) || await EInvoiceService.ApplicableFromAsync(settings, facilityId, ct) is null)
            {
                continue;
            }

            // Read when it is delivered; a reminder that could not go out by the evening is not worth sending.
            outbox.Enqueue(EInvoiceReminderHandler.MessageType, new EInvoiceReminderMessage(facilityId, today), facilityId, expiresIn: TimeSpan.FromHours(12));
            queued++;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return queued;
    }
}
