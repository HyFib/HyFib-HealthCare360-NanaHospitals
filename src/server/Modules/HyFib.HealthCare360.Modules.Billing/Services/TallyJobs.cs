using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Outbox payload: post one facility's day book for one date.</summary>
/// <param name="RunId">The post run the message belongs to; a message of an older run is ignored. Null in messages queued before runs existed.</param>
public sealed record TallyPostMessage(Guid FacilityId, DateOnly Date, Guid? RunId = null);

/// <summary>Outbox payload: reconcile one facility's posted day book with its records and with Tally.</summary>
public sealed record TallyCheckMessage(Guid FacilityId, DateOnly Date);

/// <summary>Delivers <c>billing.tally.post</c> outbox messages (retried with back-off while Tally is unreachable).</summary>
internal sealed class TallyDayBookHandler(TallyService tally) : IOutboxHandler
{
    public const string MessageType = "billing.tally.post";

    public string Type => MessageType;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        tally.PostAsync(message.Read<TallyPostMessage>(), cancellationToken);
}

/// <summary>
/// Delivers <c>billing.tally.check</c>, queued after a day book is posted: the day is compared with its records and read
/// back from Tally, and the results are kept on the day. A check that cannot reach Tally is recorded, not retried; staff
/// run it again from the Tally screen.
/// </summary>
internal sealed class TallyCheckHandler(TallyReconciliationService reconciliation) : IOutboxHandler
{
    public const string MessageType = "billing.tally.check";

    public string Type => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var check = message.Read<TallyCheckMessage>();
        await reconciliation.CheckAsync(check.FacilityId, check.Date, againstTally: true, cancellationToken);
    }
}

/// <summary>
/// Queues yesterday's day book for every facility once per day (after 00:30 IST, when late-night receipts are in).
/// It wakes hourly and is idempotent (one <see cref="TallyExport"/> row per facility and date), so a server that was
/// off at night catches up on start. A day that someone posted before it ended is posted again, so its late
/// transactions reach Tally too. Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).
/// </summary>
internal sealed class TallyDayBookScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<TallyDayBookScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeOnly EarliestRun = new(0, 30);

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
                await QueueYesterdayAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not queue the Tally day book");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task QueueYesterdayAsync(CancellationToken ct)
    {
        var localNow = BillingTime.LocalNow(time);
        if (TimeOnly.FromDateTime(localNow.DateTime) < EarliestRun)
        {
            return;
        }

        var date = DateOnly.FromDateTime(localNow.DateTime).AddDays(-1);
        var dayEnded = BillingTime.StartOfDay(date.AddDays(1));
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var facilities = await db.Set<Facility>().Where(f => f.IsActive).Select(f => f.Id).ToListAsync(ct);
        var existing = await db.Set<TallyExport>().Where(e => e.Date == date).ToDictionaryAsync(e => e.FacilityId, ct);
        var now = time.GetUtcNow();
        var queued = 0;
        foreach (var facilityId in facilities)
        {
            var export = existing.GetValueOrDefault(facilityId);
            if (export is not null && !PostedBeforeTheDayEnded(export, dayEnded))
            {
                continue;
            }

            if (!(await settings.GetAsync<bool?>(TallyService.DailyPostSetting, facilityId, ct) ?? true))
            {
                continue;
            }

            if (export is null)
            {
                export = new TallyExport { FacilityId = facilityId, Date = date, CreatedAt = now };
                db.Add(export);
            }

            export.StartRun(Guid.CreateVersion7(), now);
            outbox.Enqueue(TallyDayBookHandler.MessageType, new TallyPostMessage(facilityId, date, export.RunId), facilityId);
            queued++;
        }

        if (queued == 0)
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another worker node queued the same day first (unique facility/date).
            logger.LogInformation(ex, "Tally day book for {Date} was already queued", date);
        }
    }

    /// <summary>
    /// A run queued before the day ended sent only part of the day. One still queued will build the whole day when it
    /// runs, and a day marked imported by hand is the accountant's to post again.
    /// </summary>
    internal static bool PostedBeforeTheDayEnded(TallyExport export, DateTimeOffset dayEnded) =>
        export.Status is not (TallyExportStatus.Queued or TallyExportStatus.Imported) && (export.QueuedAt ?? export.CreatedAt) < dayEnded;
}
