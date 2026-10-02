using System.Net;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Realtime;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Notifications;

public enum NotificationChannel
{
    Sms = 0,
    WhatsApp,
    Email,
    InApp,
}

public enum NotificationStatus
{
    Queued = 0,
    Sent,
    Failed,

    /// <summary>
    /// Not attempted, and never retried: there are no contact details for the channel, or the WhatsApp template needs a
    /// document the message does not carry (the log's error says which).
    /// </summary>
    Skipped,

    /// <summary>The provider confirmed delivery to the handset or inbox (delivery receipt).</summary>
    Delivered,

    /// <summary>Deliberately not sent, and never retried: the number opted out of the channel (see the log's error).</summary>
    Suppressed,
}

/// <summary>Editable message template. Placeholders use <c>{{name}}</c>; SMS templates carry the TRAI DLT template id.</summary>
public sealed class NotificationTemplate : AuditableEntity
{
    public Guid? FacilityId { get; set; }
    public string Key { get; set; } = string.Empty;
    public NotificationChannel Channel { get; set; }
    public string Language { get; set; } = "en";
    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;
    public string? DltTemplateId { get; set; }
    public string? WhatsAppTemplateName { get; set; }

    /// <summary>
    /// The WhatsApp template was approved with a document header: messages that carry a document (a report's PDF) send
    /// it there. Set it only to match the template registered with Meta, which rejects a header it does not expect.
    /// </summary>
    public bool WhatsAppDocumentHeader { get; set; }

    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>In-app notification for a staff user (bell icon).</summary>
public sealed class UserNotification : Entity
{
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? Link { get; set; }
    public string Category { get; set; } = "general";
    public string Severity { get; set; } = "info";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Delivery record for outbound patient/staff messages (recipient stored masked).</summary>
public sealed class NotificationLog : Entity
{
    public DateTimeOffset CreatedAt { get; set; }
    public NotificationChannel Channel { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string? Recipient { get; set; }
    public Guid? PatientId { get; set; }
    public Guid? FacilityId { get; set; }
    public NotificationStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

internal sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> b)
    {
        b.ToTable("notification_templates", "platform");
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Language).HasMaxLength(8);
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.Body).HasMaxLength(4000);
        b.Property(x => x.DltTemplateId).HasMaxLength(32);
        b.Property(x => x.WhatsAppTemplateName).HasMaxLength(128);
        b.Property(x => x.Description).HasMaxLength(300);
        b.HasIndex(x => new { x.Key, x.Channel, x.Language, x.FacilityId }).IsUnique().AreNullsDistinct(false);
    }
}

internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> b)
    {
        b.ToTable("user_notifications", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Body).HasMaxLength(1000);
        b.Property(x => x.Link).HasMaxLength(500);
        b.Property(x => x.Category).HasMaxLength(32);
        b.Property(x => x.Severity).HasMaxLength(16);
        b.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
    }
}

internal sealed class NotificationLogConfiguration : IEntityTypeConfiguration<NotificationLog>
{
    public void Configure(EntityTypeBuilder<NotificationLog> b)
    {
        b.ToTable("notification_logs", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.TemplateKey).HasMaxLength(64);
        b.Property(x => x.Recipient).HasMaxLength(64);
        b.Property(x => x.ProviderMessageId).HasMaxLength(128);
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt });

        // Every delivery receipt looks its message up by the provider's id.
        b.HasIndex(x => x.ProviderMessageId);
    }
}

public sealed record NotificationRecipient(string? Phone, string? Email, Guid? PatientId, string? Name, string Language = "en");

/// <summary>
/// A document to send with a message, named rather than attached: it is made when the message goes out, so the patient
/// gets the current version, and the outbox never holds the file.
/// </summary>
/// <param name="Kind">Which <see cref="INotificationDocumentSource"/> makes it, for example "diagnostics.lab-report".</param>
public sealed record NotificationDocument(string Kind, Guid Id);

public sealed record NotificationFile(string FileName, byte[] Content, string ContentType);

/// <summary>
/// Makes the documents of one kind that messages carry (a report's PDF, say). Modules register one per kind; e-mail
/// attaches the file and WhatsApp sends it in the template's document header.
/// </summary>
public interface INotificationDocumentSource
{
    string Kind { get; }

    /// <returns>The file, or null when the document no longer exists (the message then goes without it).</returns>
    Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Outbox payload for one channel delivery.</summary>
public sealed record NotificationDelivery(
    Guid LogId,
    string TemplateKey,
    NotificationChannel Channel,
    string To,
    string? Name,
    string Language,
    IReadOnlyDictionary<string, string> Values,
    Guid? FacilityId,
    NotificationDocument? Document = null);

public interface INotificationService
{
    /// <summary>
    /// Queues a templated patient/staff message on the given channels (SMS, WhatsApp, e-mail). Delivery happens
    /// through the outbox after the caller's transaction commits, and retries through internet outages.
    /// </summary>
    /// <param name="facilityId">
    /// Facility whose templates, name and provider settings apply. Defaults to the signed-in user's facility; background
    /// jobs, which have none, should pass it.
    /// </param>
    /// <param name="document">A document to send with the message where the channel can carry one (e-mail, and WhatsApp templates with a document header).</param>
    /// <returns>The notification log ids created, one per channel (including channels skipped for missing contact details).</returns>
    IReadOnlyList<Guid> Queue(
        string templateKey,
        NotificationRecipient recipient,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<NotificationChannel> channels,
        TimeSpan? expiresIn = null,
        Guid? facilityId = null,
        NotificationDocument? document = null);

    /// <summary>Creates in-app notifications and pushes them in real time.</summary>
    Task NotifyUsersAsync(IEnumerable<Guid> userIds, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default);

    /// <summary>Notifies every active user holding <paramref name="permission"/> (optionally within a facility).</summary>
    Task NotifyPermissionHoldersAsync(string permission, Guid? facilityId, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default);
}

internal sealed class NotificationService(
    HospitalDbContext db,
    IDbContextFactory<HospitalDbContext> contextFactory,
    IOutbox outbox,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time) : INotificationService
{
    public const string SmsType = "notify.sms";
    public const string WhatsAppType = "notify.whatsapp";
    public const string EmailType = "notify.email";

    public IReadOnlyList<Guid> Queue(
        string templateKey,
        NotificationRecipient recipient,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<NotificationChannel> channels,
        TimeSpan? expiresIn = null,
        Guid? facilityId = null,
        NotificationDocument? document = null)
    {
        var now = time.GetUtcNow();
        var facility = facilityId ?? currentUser.FacilityId;
        var logIds = new List<Guid>();
        foreach (var channel in channels.Distinct().Where(c => c != NotificationChannel.InApp))
        {
            var target = channel == NotificationChannel.Email ? recipient.Email : recipient.Phone;
            var log = new NotificationLog
            {
                CreatedAt = now,
                Channel = channel,
                TemplateKey = templateKey,
                Recipient = channel == NotificationChannel.Email ? MaskEmail(target) : Phone.Mask(target),
                PatientId = recipient.PatientId,
                FacilityId = facility,
                Status = string.IsNullOrWhiteSpace(target) ? NotificationStatus.Skipped : NotificationStatus.Queued,
                Error = string.IsNullOrWhiteSpace(target) ? "No contact details for this channel." : null,
            };
            db.Add(log);
            logIds.Add(log.Id);

            if (!string.IsNullOrWhiteSpace(target))
            {
                outbox.Enqueue(
                    channel switch { NotificationChannel.Sms => SmsType, NotificationChannel.WhatsApp => WhatsAppType, _ => EmailType },
                    new NotificationDelivery(log.Id, templateKey, channel, target, recipient.Name, recipient.Language, values, facility,
                        channel == NotificationChannel.Sms ? null : document),
                    facility,
                    expiresIn: expiresIn);
            }
        }

        return logIds;
    }

    public async Task NotifyUsersAsync(IEnumerable<Guid> userIds, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var notifications = userIds.Distinct().Select(id => new UserNotification
        {
            UserId = id,
            Title = Truncate(title, 200)!,
            Body = Truncate(body, 1000),
            Link = Truncate(link, 500),
            Category = category,
            Severity = severity,
            CreatedAt = now,
        }).ToList();

        if (notifications.Count == 0)
        {
            return;
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.AddRange(notifications);
            await context.SaveChangesAsync(cancellationToken);
        }

        foreach (var notification in notifications)
        {
            await realtime.ToUserAsync(notification.UserId, "notification.new",
                new { notification.Id, notification.Title, notification.Severity, notification.Category, notification.Link }, cancellationToken);
        }
    }

    public async Task NotifyPermissionHoldersAsync(string permission, Guid? facilityId, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default)
    {
        var roleIds = db.Set<RolePermission>()
            .Where(rp => rp.Permission == permission || rp.Permission == PermissionRegistry.Wildcard)
            .Select(rp => rp.RoleId);

        var users = db.Set<User>().Where(u => u.IsActive && u.Type == UserType.Staff && u.Roles.Any(r => roleIds.Contains(r.RoleId)));
        if (facilityId is { } fid)
        {
            users = users.Where(u => u.Facilities.Count == 0 || u.Facilities.Any(f => f.FacilityId == fid));
        }

        var ids = await users.Select(u => u.Id).ToListAsync(cancellationToken);
        await NotifyUsersAsync(ids, title, body, link, category, severity, cancellationToken);
    }

    private static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            return email;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return email[0] + "***" + email[at..];
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>
/// Built-in templates seeded into the database (admins can edit wording and DLT ids). Each key must be classified in
/// <see cref="WhatsAppOptOutPolicy"/> (a unit test checks it).
/// </summary>
public static class DefaultNotificationTemplates
{
    public static IReadOnlyList<(string Key, NotificationChannel Channel, string? Subject, string Body, string Description)> All { get; } =
    [
        ("auth.otp", NotificationChannel.Sms, null, "{{code}} is your {{hospital}} verification code. It expires in {{minutes}} minutes. Do not share it with anyone.", "Patient portal sign-in OTP"),
        ("appointment.confirmed", NotificationChannel.Sms, null, "Dear {{name}}, your appointment with {{doctor}} is confirmed for {{date}} at {{time}}. Ref {{reference}}. - {{hospital}}", "Appointment booked"),
        ("appointment.confirmed", NotificationChannel.WhatsApp, null, "Dear {{name}}, your appointment with {{doctor}} is confirmed for {{date}} at {{time}}. Reference: {{reference}}.", "Appointment booked (WhatsApp)"),
        ("appointment.reminder", NotificationChannel.Sms, null, "Reminder: {{name}}, your appointment with {{doctor}} is on {{date}} at {{time}}. - {{hospital}}", "Appointment reminder"),
        ("appointment.reminder", NotificationChannel.WhatsApp, null, "Reminder: your appointment with {{doctor}} is on {{date}} at {{time}}.", "Appointment reminder (WhatsApp)"),
        ("appointment.cancelled", NotificationChannel.Sms, null, "Dear {{name}}, your appointment with {{doctor}} on {{date}} at {{time}} has been cancelled. - {{hospital}}", "Appointment cancelled"),
        ("visit.token", NotificationChannel.Sms, null, "Your token for {{department}} is {{token}}. Please wait for your turn. - {{hospital}}", "Queue token issued"),
        ("bill.receipt", NotificationChannel.Sms, null, "Payment of Rs.{{amount}} received for {{billNumber}}. Thank you. - {{hospital}}", "Payment receipt"),
        ("payment.link", NotificationChannel.Sms, null, "Dear {{name}}, pay Rs.{{amount}} for {{purpose}} securely: {{link}} - {{hospital}}", "Payment link"),
        ("payment.link", NotificationChannel.WhatsApp, null, "Dear {{name}}, please pay Rs.{{amount}} for {{purpose}} using this secure link: {{link}}", "Payment link (WhatsApp)"),
        ("lab.report_ready", NotificationChannel.Sms, null, "Dear {{name}}, your report {{reference}} is ready. View it on the patient portal. - {{hospital}}", "Report ready"),
        ("lab.report_ready", NotificationChannel.WhatsApp, null, "Dear {{name}}, your report {{reference}} is ready. It is also available on the patient portal.", "Report ready (WhatsApp)"),
        ("telehealth.link", NotificationChannel.Sms, null, "Dear {{name}}, join your video consultation with {{doctor}} at {{time}}: {{link}} - {{hospital}}", "Telehealth join link"),
        ("telehealth.link", NotificationChannel.WhatsApp, null, "Dear {{name}}, join your video consultation with {{doctor}} at {{time}} using this link: {{link}}", "Telehealth join link (WhatsApp)"),
        ("admission.admitted", NotificationChannel.Sms, null, "{{name}} has been admitted to {{ward}} bed {{bed}} under {{doctor}}. - {{hospital}}", "Admission confirmation"),
        ("discharge.completed", NotificationChannel.Sms, null, "{{name}} has been discharged. Your discharge summary is on the patient portal. Get well soon. - {{hospital}}", "Discharge completed"),
        ("prescription.shared", NotificationChannel.WhatsApp, null, "Dear {{name}}, your prescription from {{doctor}} dated {{date}} is available on the patient portal.", "Prescription shared"),
        ("feedback.request", NotificationChannel.Sms, null, "Dear {{name}}, how was your visit? Please share feedback: {{link}} - {{hospital}}", "Feedback request"),
        ("followup.reminder", NotificationChannel.Sms, null, "Dear {{name}}, your follow-up with {{doctor}} is due on {{date}}. Book at {{link}} - {{hospital}}", "Follow-up reminder"),
        ("rpm.alert", NotificationChannel.Sms, null, "HC360 RPM alert: {{patient}} reported {{reading}} (outside set range) at {{time}}.", "Remote monitoring alert to care team"),
        ("lab.critical", NotificationChannel.Sms, null, "HC360 CRITICAL LAB: {{patient}} ({{uhid}}) {{test}} = {{value}}. Please review immediately.", "Critical lab value to doctor"),
        ("system.alert", NotificationChannel.Sms, null, "HC360 server alert ({{server}}): {{message}}", "Server Node alert to IT on-call (WiFi down, backup failed…)"),
    ];
}

internal sealed class NotificationTemplateSeeder : ISeeder
{
    public int Order => 20;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Set<NotificationTemplate>()
            .Where(t => t.FacilityId == null && t.Language == "en")
            .Select(t => new { t.Key, t.Channel })
            .ToListAsync(cancellationToken);

        foreach (var template in DefaultNotificationTemplates.All.Where(t => !existing.Any(e => e.Key == t.Key && e.Channel == t.Channel)))
        {
            db.Add(new NotificationTemplate
            {
                Key = template.Key,
                Channel = template.Channel,
                Subject = template.Subject,
                Body = template.Body,
                Description = template.Description,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Resolves the best template (facility → global, language → English) and renders placeholders.</summary>
public sealed partial class NotificationTemplateRenderer(HospitalDbContext db, IMemoryCache cache)
{
    public async Task<(NotificationTemplate? Template, string? Subject, string Body)> RenderAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var candidates = await db.Set<NotificationTemplate>().AsNoTracking()
            .Where(t => t.Key == delivery.TemplateKey && t.Channel == delivery.Channel && t.IsActive
                        && (t.FacilityId == null || t.FacilityId == delivery.FacilityId)
                        && (t.Language == delivery.Language || t.Language == "en"))
            .ToListAsync(cancellationToken);

        var template = candidates
            .OrderByDescending(t => t.FacilityId == delivery.FacilityId && t.FacilityId != null)
            .ThenByDescending(t => t.Language == delivery.Language)
            .FirstOrDefault();

        var values = new Dictionary<string, string>(delivery.Values, StringComparer.OrdinalIgnoreCase);
        if (!values.ContainsKey("hospital"))
        {
            values["hospital"] = await FacilityNameAsync(delivery.FacilityId, cancellationToken) ?? "Hospital";
        }

        if (!values.ContainsKey("name") && delivery.Name is not null)
        {
            values["name"] = delivery.Name;
        }

        var body = template?.Body ?? DefaultNotificationTemplates.All
            .FirstOrDefault(t => t.Key == delivery.TemplateKey && t.Channel == delivery.Channel).Body
            ?? throw new InvalidOperationException($"No template '{delivery.TemplateKey}' for {delivery.Channel}.");

        var html = delivery.Channel == NotificationChannel.Email;
        return (template, template?.Subject is { } subject ? Render(subject, values, false) : null, Render(body, values, html));
    }

    internal static string Render(string text, IReadOnlyDictionary<string, string> values, bool htmlEncode) =>
        PlaceholderRegex().Replace(text, m =>
        {
            var value = values.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty;
            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });

    private Task<string?> FacilityNameAsync(Guid? facilityId, CancellationToken ct) =>
        cache.GetOrCreateAsync($"facility-name:{facilityId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var query = db.Set<Facility>().AsNoTracking();
            return facilityId is { } id
                ? await query.Where(f => f.Id == id).Select(f => f.Name).FirstOrDefaultAsync(ct)
                : await query.OrderBy(f => f.CreatedAt).Select(f => f.Name).FirstOrDefaultAsync(ct);
        });

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}

/// <summary>Outbox handlers that render templates and call the configured channel provider.</summary>
internal abstract class NotificationDeliveryHandler(
    HospitalDbContext db, NotificationTemplateRenderer renderer, IEnumerable<INotificationDocumentSource> documents, TimeProvider time, ILogger logger)
    : IOutboxHandler
{
    // A document that can't be made (the PDF browser restarting, say) is tried this many times before a message that
    // can go without it is sent without it: the patient still hears that the report is ready.
    private const int DocumentAttempts = 3;

    internal const string NoDocumentForHeader =
        "Not sent: the WhatsApp template has a document header, and this message carries no document. Untick \"Document header\" on the template to send it without one.";

    public abstract string Type { get; }

    protected HospitalDbContext Db => db;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var delivery = message.Read<NotificationDelivery>();
        var log = await db.Set<NotificationLog>().FirstOrDefaultAsync(l => l.Id == delivery.LogId, cancellationToken);
        if (await SuppressionReasonAsync(delivery, cancellationToken) is { } reason)
        {
            if (log is not null)
            {
                log.Status = NotificationStatus.Suppressed;
                log.Error = reason;
                await db.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation("Notification {TemplateKey} via {Channel} not sent: {Reason}", delivery.TemplateKey, delivery.Channel, reason);
            return;
        }

        var (template, subject, body) = await renderer.RenderAsync(delivery, cancellationToken);
        if (NeedsDocument(template) && delivery.Document is null)
        {
            // Meta refuses a template approved with a document header when there is no document, so sending would only
            // fail and be retried (say, report PDFs are switched off but the template still has its header). It is
            // skipped instead, and the log says how to fix it. The message's other channels still go.
            if (log is not null)
            {
                log.Status = NotificationStatus.Skipped;
                log.Error = NoDocumentForHeader;
                await db.SaveChangesAsync(cancellationToken);
            }

            logger.LogWarning("Notification {TemplateKey} via {Channel} not sent: its template needs a document", delivery.TemplateKey, delivery.Channel);
            return;
        }

        var (file, missing) = await DocumentAsync(delivery, template, message.Attempts, cancellationToken);

        var result = await SendAsync(delivery, template, subject, body, file, cancellationToken);
        if (log is not null)
        {
            log.Status = result.Success ? NotificationStatus.Sent : NotificationStatus.Failed;
            log.ProviderMessageId = result.ProviderMessageId;
            // Providers quote the recipient back in their errors (Twilio: "The 'To' number +91… is not valid").
            log.Error = SensitiveText.RedactOrNull(result.Error) ?? missing;
            log.SentAt = result.Success ? time.GetUtcNow() : null;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!result.Success)
        {
            logger.LogWarning("Notification {TemplateKey} via {Channel} failed: {Error}", delivery.TemplateKey, delivery.Channel, result.Error);
            throw new InvalidOperationException(result.Error ?? "Delivery failed.");
        }
    }

    /// <summary>Why this message must not be sent at all (it is then marked suppressed, not retried), or null to send it.</summary>
    protected virtual Task<string?> SuppressionReasonAsync(NotificationDelivery delivery, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>Whether this channel sends the message's document with this template.</summary>
    protected virtual bool CarriesDocument(NotificationTemplate? template) => false;

    /// <summary>Whether the template can't be sent without its document (a WhatsApp document header can't be left empty).</summary>
    protected virtual bool NeedsDocument(NotificationTemplate? template) => false;

    protected abstract Task<SendResult> SendAsync(
        NotificationDelivery delivery, NotificationTemplate? template, string? subject, string body, NotificationFile? document, CancellationToken cancellationToken);

    /// <summary>The document to send, and a note for the log when the message goes without the one it names.</summary>
    private async Task<(NotificationFile? File, string? Missing)> DocumentAsync(
        NotificationDelivery delivery, NotificationTemplate? template, int attempts, CancellationToken cancellationToken)
    {
        if (delivery.Document is not { } reference || !CarriesDocument(template))
        {
            return (null, null);
        }

        if (documents.FirstOrDefault(d => d.Kind == reference.Kind) is not { } source)
        {
            return NeedsDocument(template)
                ? throw new InvalidOperationException($"Nothing makes '{reference.Kind}' documents.")
                : (null, $"Sent without the document: nothing makes '{reference.Kind}' documents.");
        }

        try
        {
            if (await source.CreateAsync(reference.Id, cancellationToken) is { } file)
            {
                return (file, null);
            }

            return NeedsDocument(template)
                ? throw new InvalidOperationException("The document to send no longer exists.")
                : (null, "Sent without the document: it no longer exists.");
        }
        catch (PdfRenderException ex) when (attempts + 1 >= DocumentAttempts && !NeedsDocument(template))
        {
            logger.LogWarning(ex, "Notification {TemplateKey} via {Channel} goes without its document", delivery.TemplateKey, delivery.Channel);
            return (null, $"Sent without the document: {ex.Message}");
        }
    }
}

internal sealed class SmsDeliveryHandler(
    HospitalDbContext db, NotificationTemplateRenderer renderer, IEnumerable<INotificationDocumentSource> documents, ISmsSender sms, TimeProvider time,
    ILogger<SmsDeliveryHandler> logger)
    : NotificationDeliveryHandler(db, renderer, documents, time, logger)
{
    public override string Type => NotificationService.SmsType;

    protected override Task<SendResult> SendAsync(
        NotificationDelivery delivery, NotificationTemplate? template, string? subject, string body, NotificationFile? document, CancellationToken cancellationToken) =>
        sms.SendAsync(new SmsMessage(delivery.To, body, template?.DltTemplateId, null, delivery.LogId.ToString("N"), delivery.FacilityId), cancellationToken);
}

internal sealed class WhatsAppDeliveryHandler(
    HospitalDbContext db, NotificationTemplateRenderer renderer, IEnumerable<INotificationDocumentSource> documents, IWhatsAppSender whatsApp,
    TimeProvider time, ILogger<WhatsAppDeliveryHandler> logger)
    : NotificationDeliveryHandler(db, renderer, documents, time, logger)
{
    public override string Type => NotificationService.WhatsAppType;

    /// <summary>
    /// A number that replied STOP gets only essential messages on WhatsApp. The check runs at delivery time, so messages
    /// queued before the STOP, and reminders queued for later, stop too.
    /// </summary>
    protected override async Task<string?> SuppressionReasonAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (WhatsAppOptOutPolicy.IsEssential(delivery.TemplateKey) || Phone.Normalize(delivery.To) is not { } phone)
        {
            return null;
        }

        var optedOut = await Db.Set<WhatsAppOptOut>().AnyAsync(WhatsAppOptOutPolicy.InForceFor(phone), cancellationToken);
        return WhatsAppOptOutPolicy.Stops(delivery.Channel, delivery.TemplateKey, optedOut) ? WhatsAppOptOutPolicy.SuppressedReason : null;
    }

    protected override bool CarriesDocument(NotificationTemplate? template) => template?.WhatsAppDocumentHeader == true;

    protected override bool NeedsDocument(NotificationTemplate? template) => template?.WhatsAppDocumentHeader == true;

    protected override Task<SendResult> SendAsync(
        NotificationDelivery delivery, NotificationTemplate? template, string? subject, string body, NotificationFile? document, CancellationToken cancellationToken) =>
        whatsApp.SendTemplateAsync(
            new WhatsAppTemplateMessage(delivery.To, template?.WhatsAppTemplateName ?? delivery.TemplateKey.Replace('.', '_'), delivery.Language,
                [body], delivery.LogId.ToString("N"), delivery.FacilityId,
                document is null ? null : new WhatsAppHeaderDocument(document.FileName, document.Content, document.ContentType)),
            cancellationToken);
}

internal sealed class EmailDeliveryHandler(
    HospitalDbContext db, NotificationTemplateRenderer renderer, IEnumerable<INotificationDocumentSource> documents, IEmailSender email, TimeProvider time,
    ILogger<EmailDeliveryHandler> logger)
    : NotificationDeliveryHandler(db, renderer, documents, time, logger)
{
    public override string Type => NotificationService.EmailType;

    protected override bool CarriesDocument(NotificationTemplate? template) => true;

    protected override Task<SendResult> SendAsync(
        NotificationDelivery delivery, NotificationTemplate? template, string? subject, string body, NotificationFile? document, CancellationToken cancellationToken) =>
        email.SendAsync(
            new EmailMessage(delivery.To, subject ?? "Message from the hospital", body,
                document is null ? null : [new EmailAttachment(document.FileName, document.Content, document.ContentType)],
                delivery.LogId.ToString("N"), delivery.FacilityId),
            cancellationToken);
}

/// <summary>Fallback channel providers used until an integration is configured: they log and report failure, never silently succeed.</summary>
internal sealed class UnconfiguredChannelSender(ILogger<UnconfiguredChannelSender> logger) : ISmsSender, IWhatsAppSender, IEmailSender
{
    public string Provider => "unconfigured";

    public Task<SendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken) => NotConfigured("SMS");

    public Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken) => NotConfigured("WhatsApp");

    public Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, CancellationToken cancellationToken) => NotConfigured("WhatsApp");

    public Task<SendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken) => NotConfigured("WhatsApp");

    public Task<SendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken) => NotConfigured("e-mail");

    private Task<SendResult> NotConfigured(string channel)
    {
        logger.LogWarning("No {Channel} provider is configured; message not sent", channel);
        return Task.FromResult(SendResult.Fail($"No {channel} provider is configured. Set one up in Administration → Integrations."));
    }
}
