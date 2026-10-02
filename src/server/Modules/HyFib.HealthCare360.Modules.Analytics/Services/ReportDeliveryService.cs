using System.Globalization;
using System.Net;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Reports;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Export;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Generates one delivery of a schedule: runs the report as the schedule's owner (current permissions, the schedule's
/// facility), writes the Excel, CSV or PDF file, keeps it encrypted, and queues one e-mail per recipient through the
/// platform outbox (<see cref="ScheduledReportEmailHandler"/>), so e-mails survive internet outages and are retried with
/// back-off. Everything — delivery, recipients, outbox messages, schedule status and the audit entry — commits in one
/// transaction.
/// </summary>
internal sealed class ReportDeliveryService(
    HospitalDbContext db,
    ReportCatalog catalog,
    ReportActors actors,
    ReportRunner runner,
    ReportPresenter presenter,
    ReportFileWriter files,
    ReportAuditor auditor,
    IFieldProtector protector,
    IOutbox outbox,
    TimeProvider time,
    ILogger<ReportDeliveryService> logger)
{
    /// <summary>Encryption purpose of delivered files.</summary>
    public const string FilePurpose = "analytics.delivery";

    /// <summary>E-mails not delivered within this time are dropped (the file is still downloadable in HC360).</summary>
    public static readonly TimeSpan EmailExpiry = TimeSpan.FromDays(3);

    /// <summary>Generates and queues a delivery for <paramref name="schedule"/>, which must be tracked by this scope's context.</summary>
    public async Task<ReportDelivery> DeliverAsync(ReportSchedule schedule, DeliveryTrigger trigger, Guid? requestedBy, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var delivery = new ReportDelivery
        {
            FacilityId = schedule.FacilityId,
            ScheduleId = schedule.Id,
            ReportId = schedule.ReportId,
            ReportTitle = catalog.Find(schedule.ReportId)?.Descriptor.Title ?? schedule.ReportId,
            Trigger = trigger,
            RequestedBy = requestedBy,
            CreatedAt = now,
            Status = DeliveryStatus.Generating,
            Format = schedule.Format,
        };
        db.Add(delivery);

        var actor = await actors.ForUserAsync(schedule.OwnerUserId, schedule.FacilityId, ct);
        if (actor is null)
        {
            return await FinishAsync(schedule, delivery, now, DeliveryStatus.Skipped,
                "The person who scheduled this report can no longer sign in. Ask a colleague to open the schedule and save it again.", ct);
        }

        var parameters = ScheduleParameters.Read(schedule.ParametersJson);
        parameters[ReportKeys.Facility] = schedule.FacilityId.ToString();
        var execution = await runner.RunAsync(actor, schedule.ReportId, parameters, ct);
        if (execution.IsFailure)
        {
            return await FinishAsync(schedule, delivery, now, DeliveryStatus.Failed, execution.Error!.Message, ct);
        }

        if (execution.Value.Report.ContainsPatientIdentifiers)
        {
            return await FinishAsync(schedule, delivery, now, DeliveryStatus.Failed, "Reports that list patients cannot be e-mailed.", ct);
        }

        var filters = await presenter.DescribeAsync(execution.Value, ct);
        var document = presenter.Document(execution.Value, filters, $"{ReportEndpoints.Person(actor)} (scheduled e-mail \"{schedule.Name}\")", null, null);
        ReportFile file;
        try
        {
            file = await files.WriteAsync(document, schedule.Format, ct);
        }
        catch (PdfRenderException ex)
        {
            // Reported like a report that could not run: the delivery fails with the reason, the schedule shows it,
            // and the e-mail goes at the next scheduled time (or with Send now).
            return await FinishAsync(schedule, delivery, now, DeliveryStatus.Failed,
                $"The PDF could not be made: {ex.Message} Use Send now to try again, or choose Excel or CSV for this schedule.", ct);
        }

        delivery.FileName = file.FileName;
        delivery.ContentType = file.ContentType;
        delivery.SizeBytes = file.Content.LongLength;
        delivery.RowCount = file.Rows;
        delivery.Content = protector.ProtectBytes(file.Content, FilePurpose);
        delivery.GeneratedAt = now;
        delivery.Status = DeliveryStatus.Ready;
        if (execution.Value.Report.HasPeriod)
        {
            delivery.PeriodFrom = execution.Value.Arguments.From;
            delivery.PeriodTo = execution.Value.Arguments.To;
        }

        foreach (var email in schedule.Recipients)
        {
            var recipient = new ReportDeliveryRecipient { DeliveryId = delivery.Id, Email = email, Status = RecipientStatus.Pending };
            delivery.Recipients.Add(recipient);
            outbox.Enqueue(ScheduledReportEmailHandler.MessageType, new ScheduledReportEmail(delivery.Id, recipient.Id), schedule.FacilityId, expiresIn: EmailExpiry);
        }

        schedule.RecordRun(now, ScheduleRunStatus.Succeeded, null);

        // Saves the whole unit of work (delivery, recipients, outbox messages, schedule) together with the audit entry.
        await auditor.ExportedAsync(execution.Value, schedule.Format, file.Rows, actor, $"e-mail to {schedule.Recipients.Count} recipient(s)", [], ct);
        logger.LogInformation("Scheduled report {ScheduleId} generated ({Rows} rows) and queued for {Recipients} recipient(s)", schedule.Id, file.Rows, schedule.Recipients.Count);
        return delivery;
    }

    public static DeliveryDto ToDto(ReportDelivery d) => new(
        d.Id,
        d.ScheduleId,
        d.ReportTitle,
        d.Trigger,
        d.CreatedAt,
        d.Status,
        d.PeriodFrom,
        d.PeriodTo,
        d.Format,
        d.FileName,
        d.SizeBytes,
        d.RowCount,
        d.Content is not null,
        d.Error,
        d.Recipients.Count(r => r.Status == RecipientStatus.Sent),
        d.Recipients.Count(r => r.Status == RecipientStatus.Pending),
        d.Recipients.OrderBy(r => r.Email, StringComparer.Ordinal).Select(r => new DeliveryRecipientDto(r.Email, r.Status, r.Attempts, r.LastError, r.SentAt)).ToList());

    private async Task<ReportDelivery> FinishAsync(
        ReportSchedule schedule, ReportDelivery delivery, DateTimeOffset now, DeliveryStatus status, string error, CancellationToken ct)
    {
        delivery.Fail(error, status);
        schedule.RecordRun(now, status == DeliveryStatus.Skipped ? ScheduleRunStatus.Skipped : ScheduleRunStatus.Failed, error);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Scheduled report {ScheduleId} was not delivered: {Error}", schedule.Id, error);
        return delivery;
    }
}

/// <summary>Outbox payload: one e-mail of one delivery.</summary>
public sealed record ScheduledReportEmail(Guid DeliveryId, Guid RecipientId);

/// <summary>
/// Sends one scheduled-report e-mail with the file attached, through the platform <see cref="IEmailSender"/> (the
/// notification service's <c>Queue</c> contract cannot carry attachments). Wording comes from the editable e-mail
/// template <see cref="TemplateKey"/>. Idempotent per recipient; throws to let the outbox retry when sending fails.
/// </summary>
internal sealed class ScheduledReportEmailHandler(
    HospitalDbContext db,
    IEmailSender email,
    NotificationTemplateRenderer renderer,
    IFieldProtector protector,
    TimeProvider time,
    ILogger<ScheduledReportEmailHandler> logger) : IOutboxHandler
{
    public const string MessageType = "analytics.report.email";
    public const string TemplateKey = "analytics.scheduled_report";

    public string Type => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Read<ScheduledReportEmail>();
        var recipient = await db.Set<ReportDeliveryRecipient>()
            .FirstOrDefaultAsync(r => r.Id == payload.RecipientId && r.DeliveryId == payload.DeliveryId, cancellationToken);
        if (recipient is null || recipient.Status != RecipientStatus.Pending)
        {
            return;
        }

        var delivery = await db.Set<ReportDelivery>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == payload.DeliveryId, cancellationToken);
        var schedule = delivery is null
            ? null
            : await db.Set<ReportSchedule>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == delivery.ScheduleId, cancellationToken);
        if (delivery?.Content is null || schedule is null)
        {
            recipient.Status = RecipientStatus.Cancelled;
            recipient.LastError = schedule is null ? "The schedule was deleted before the e-mail was sent." : "The file was removed before the e-mail was sent.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var content = protector.UnprotectBytes(delivery.Content, ReportDeliveryService.FilePurpose);
        var (subject, body) = await ComposeAsync(delivery, schedule, recipient.Email, cancellationToken);
        var attach = content.LongLength <= AnalyticsSettings.MaxAttachmentBytes;
        if (!attach)
        {
            body += "<p>The file is too large to attach. Download it in HC360 under Analytics → Scheduled e-mails.</p>";
        }

        var result = await email.SendAsync(
            new EmailMessage(
                recipient.Email,
                subject,
                body,
                attach ? [new EmailAttachment(delivery.FileName ?? "report", content, delivery.ContentType ?? "application/octet-stream")] : null,
                $"analytics-{recipient.Id:N}"),
            cancellationToken);

        recipient.RecordAttempt(result.Success, result.Error, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("Scheduled report e-mail {RecipientId} failed: {Error}", recipient.Id, result.Error);
            throw new InvalidOperationException(result.Error ?? "The e-mail could not be sent.");
        }
    }

    private async Task<(string Subject, string Body)> ComposeAsync(ReportDelivery delivery, ReportSchedule schedule, string to, CancellationToken ct)
    {
        var period = delivery.PeriodFrom is { } periodFrom && delivery.PeriodTo is { } periodTo
            ? ReportPeriods.Describe(periodFrom, periodTo)
            : AnalyticsClock.FormatDate(AnalyticsClock.LocalDate(delivery.CreatedAt));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["report"] = delivery.ReportTitle,
            ["schedule"] = schedule.Name,
            ["period"] = period,
            ["generatedAt"] = AnalyticsClock.FormatDateTime(delivery.GeneratedAt ?? delivery.CreatedAt),
            ["rows"] = delivery.RowCount.ToString("N0", CultureInfo.InvariantCulture),
            ["fileName"] = delivery.FileName ?? string.Empty,
        };

        try
        {
            var (_, subject, body) = await renderer.RenderAsync(
                new NotificationDelivery(Guid.Empty, TemplateKey, NotificationChannel.Email, to, null, "en", values, delivery.FacilityId), ct);
            return (subject ?? $"{delivery.ReportTitle} — {period}", body);
        }
        catch (InvalidOperationException)
        {
            // Template not seeded yet (first start): fall back to the built-in wording.
            var report = WebUtility.HtmlEncode(delivery.ReportTitle);
            return ($"{delivery.ReportTitle} — {period}",
                $"<p>Please find attached the <strong>{report}</strong> report for <strong>{WebUtility.HtmlEncode(period)}</strong>.</p>"
                + "<p>This report is confidential. Please do not forward it outside the hospital.</p><p>— HyFib HealthCare360</p>");
        }
    }
}

/// <summary>
/// Sends due scheduled report e-mails (checked every minute) and purges delivered files after the retention period.
/// Runs only where <see cref="PlatformOptions.RunsJobs"/>. A run is claimed by moving the schedule's next run time forward
/// under its concurrency version, so with several worker nodes each e-mail is generated once; a server that was off
/// sends one catch-up e-mail per schedule, not one per missed day.
/// </summary>
internal sealed class ScheduledReportJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<ScheduledReportJob> logger) : BackgroundService
{
    private const int BatchSize = 10;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(6);
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

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
                await RunDueAsync(stoppingToken);
                if (time.GetUtcNow() - _lastPurge >= PurgeEvery)
                {
                    await PurgeAsync(stoppingToken);
                    _lastPurge = time.GetUtcNow();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled report e-mails could not be processed; they will be retried");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> RunDueAsync(CancellationToken ct)
    {
        List<Guid> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var now = time.GetUtcNow();
            due = await db.Set<ReportSchedule>().AsNoTracking()
                .Where(s => s.IsActive && s.NextRunAt != null && s.NextRunAt <= now)
                .OrderBy(s => s.NextRunAt)
                .Select(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
        }

        var delivered = 0;
        foreach (var scheduleId in due)
        {
            if (await RunOneAsync(scheduleId, ct))
            {
                delivered++;
            }
        }

        return delivered;
    }

    private async Task<bool> RunOneAsync(Guid scheduleId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<HospitalDbContext>();
        var now = time.GetUtcNow();
        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == scheduleId && s.IsActive, ct);
        if (schedule?.NextRunAt is not { } dueAt || dueAt > now)
        {
            return false;
        }

        schedule.Reschedule(now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker node claimed this run.
            return false;
        }

        try
        {
            var delivery = await services.GetRequiredService<ReportDeliveryService>().DeliverAsync(schedule, DeliveryTrigger.Scheduled, null, ct);
            return delivery.Status == DeliveryStatus.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled report {ScheduleId} failed", scheduleId);
            await RecordFailureAsync(scheduleId, ex, ct);
            return false;
        }
    }

    private async Task RecordFailureAsync(Guid scheduleId, Exception error, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (schedule is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        schedule.RecordRun(now, ScheduleRunStatus.Failed, "The report could not be generated. It will run again at the next scheduled time.");
        db.Add(new ReportDelivery
        {
            FacilityId = schedule.FacilityId,
            ScheduleId = schedule.Id,
            ReportId = schedule.ReportId,
            ReportTitle = scope.ServiceProvider.GetRequiredService<ReportCatalog>().Find(schedule.ReportId)?.Descriptor.Title ?? schedule.ReportId,
            CreatedAt = now,
            Format = schedule.Format,
            Status = DeliveryStatus.Failed,
            Error = error.GetType().Name + ": the report could not be generated.",
        });
        await db.SaveChangesAsync(ct);
    }

    internal async Task<int> PurgeAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var days = Math.Clamp(await settings.GetAsync<int?>(AnalyticsSettings.DeliveryRetentionDays, null, ct) ?? AnalyticsSettings.DefaultRetentionDays, 1, 3650);
        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-days);
        var purged = await db.Set<ReportDelivery>()
            .Where(d => d.CreatedAt < cutoff && d.Content != null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Content, (byte[]?)null).SetProperty(d => d.ContentPurgedAt, now), ct);
        if (purged > 0)
        {
            logger.LogInformation("Purged {Count} scheduled report file(s) older than {Days} days", purged, days);
        }

        return purged;
    }
}
