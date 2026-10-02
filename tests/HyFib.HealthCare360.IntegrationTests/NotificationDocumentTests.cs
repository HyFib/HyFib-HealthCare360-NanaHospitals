using System.Text.Json;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The documents messages carry (a report's PDF): attached to e-mails, sent in WhatsApp document headers, and what
/// happens when one can't be made. The delivery handlers run against the test server's database with fake senders; the
/// outbox messages are built in memory, so the server's own dispatcher never sees them.
/// </summary>
public sealed class NotificationDocumentTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly NotificationFile Report = new("lab-report-NHLAB2600012.pdf", [0x25, 0x50, 0x44, 0x46], "application/pdf");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_email_carries_the_named_document_as_an_attachment()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var (key, kind) = await TemplatesAsync(scope, whatsAppHeader: false);
        var email = new FakeEmail();
        var (delivery, logId) = await DeliveryAsync(scope, key, NotificationChannel.Email, "asha@example.org", kind);

        await Email(scope, email, Source(kind, _ => Report)).HandleAsync(Message(NotificationService.EmailType, delivery), Ct);

        var attachment = email.Sent.ShouldHaveSingleItem().Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        attachment.FileName.ShouldBe(Report.FileName);
        attachment.ContentType.ShouldBe("application/pdf");
        attachment.Content.ShouldBe(Report.Content);
        var log = await LogAsync(scope, logId);
        log.Status.ShouldBe(NotificationStatus.Sent);
        log.Error.ShouldBeNull();
    }

    [Fact]
    public async Task An_email_whose_document_cannot_be_made_is_retried_and_then_sent_without_it()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var (key, kind) = await TemplatesAsync(scope, whatsAppHeader: false);
        var email = new FakeEmail();
        var failing = Source(kind, _ => throw new PdfRenderException("Making the PDF took too long."));
        var (delivery, logId) = await DeliveryAsync(scope, key, NotificationChannel.Email, "asha@example.org", kind);

        // The first tries fail, so the outbox retries later: the PDF browser may just be restarting.
        await Should.ThrowAsync<PdfRenderException>(() => Email(scope, email, failing).HandleAsync(Message(NotificationService.EmailType, delivery, attempts: 0), Ct));
        email.Sent.ShouldBeEmpty();

        // The third goes without the document: the patient still hears that the report is ready.
        await Email(scope, email, failing).HandleAsync(Message(NotificationService.EmailType, delivery, attempts: 2), Ct);

        email.Sent.ShouldHaveSingleItem().Attachments.ShouldBeNull();
        var log = await LogAsync(scope, logId);
        log.Status.ShouldBe(NotificationStatus.Sent);
        log.Error.ShouldBe("Sent without the document: Making the PDF took too long.");
    }

    [Fact]
    public async Task A_whatsapp_template_with_a_document_header_sends_the_document_there_and_never_goes_without_it()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var (key, kind) = await TemplatesAsync(scope, whatsAppHeader: true);
        var whatsApp = new FakeWhatsApp();
        var (delivery, _) = await DeliveryAsync(scope, key, NotificationChannel.WhatsApp, "+919876543210", kind);

        await WhatsApp(scope, whatsApp, Source(kind, _ => Report)).HandleAsync(Message(NotificationService.WhatsAppType, delivery), Ct);

        var header = whatsApp.Sent.ShouldHaveSingleItem().HeaderDocument.ShouldNotBeNull();
        header.FileName.ShouldBe(Report.FileName);
        header.Content.ShouldBe(Report.Content);

        // A header can't be left empty, so the message waits for its document however many tries it takes.
        var failing = Source(kind, _ => throw new PdfRenderException("No browser."));
        await Should.ThrowAsync<PdfRenderException>(() =>
            WhatsApp(scope, whatsApp, failing).HandleAsync(Message(NotificationService.WhatsAppType, delivery, attempts: 5), Ct));
        whatsApp.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_whatsapp_template_without_a_document_header_never_carries_one()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var (key, kind) = await TemplatesAsync(scope, whatsAppHeader: false);
        var whatsApp = new FakeWhatsApp();
        var made = 0;
        var (delivery, _) = await DeliveryAsync(scope, key, NotificationChannel.WhatsApp, "+919876543210", kind);

        await WhatsApp(scope, whatsApp, Source(kind, _ => { made++; return Report; })).HandleAsync(Message(NotificationService.WhatsAppType, delivery), Ct);

        whatsApp.Sent.ShouldHaveSingleItem().HeaderDocument.ShouldBeNull();
        made.ShouldBe(0, "The document is not even made when the channel can't carry it.");
    }

    [Fact]
    public async Task Text_messages_are_queued_without_the_document()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var document = new NotificationDocument("diagnostics.lab-report", Guid.NewGuid());

        // Queued only in this unit of work, which is never saved.
        notifications.Queue("lab.report_ready", new NotificationRecipient("+919876543210", "asha@example.org", null, "Asha"),
            new Dictionary<string, string> { ["reference"] = "NHLAB2600012" },
            [NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Email], document: document);

        var queued = db.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity.Read<NotificationDelivery>()).ToDictionary(d => d.Channel);
        queued[NotificationChannel.Sms].Document.ShouldBeNull();
        queued[NotificationChannel.WhatsApp].Document.ShouldBe(document);
        queued[NotificationChannel.Email].Document.ShouldBe(document);
    }

    // Templates of this test alone (unique key), and a document kind no module makes.
    private static async Task<(string Key, string Kind)> TemplatesAsync(AsyncServiceScope scope, bool whatsAppHeader)
    {
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var key = "test.doc_" + Guid.NewGuid().ToString("N")[..10];
        db.Add(new NotificationTemplate { Key = key, Channel = NotificationChannel.Email, Subject = "Your report {{reference}}", Body = "Dear {{name}}, your report is ready." });
        db.Add(new NotificationTemplate
        {
            Key = key,
            Channel = NotificationChannel.WhatsApp,
            Body = "Dear {{name}}, your report {{reference}} is ready.",
            WhatsAppTemplateName = "lab_report_ready",
            WhatsAppDocumentHeader = whatsAppHeader,
        });
        await db.SaveChangesAsync(Ct);
        return (key, "test." + key);
    }

    private static async Task<(NotificationDelivery Delivery, Guid LogId)> DeliveryAsync(
        AsyncServiceScope scope, string key, NotificationChannel channel, string to, string kind)
    {
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var log = new NotificationLog { CreatedAt = DateTimeOffset.UtcNow, Channel = channel, TemplateKey = key, Status = NotificationStatus.Queued };
        db.Add(log);
        await db.SaveChangesAsync(Ct);
        var delivery = new NotificationDelivery(log.Id, key, channel, to, "Asha", "en", new Dictionary<string, string> { ["reference"] = "NHLAB2600012" },
            null, new NotificationDocument(kind, Guid.NewGuid()));
        return (delivery, log.Id);
    }

    private static async Task<NotificationLog> LogAsync(AsyncServiceScope scope, Guid id) =>
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking().SingleAsync(l => l.Id == id, Ct);

    private static OutboxMessage Message(string type, NotificationDelivery delivery, int attempts = 0) =>
        new() { Type = type, Payload = JsonSerializer.Serialize(delivery, Json), Attempts = attempts };

    private static EmailDeliveryHandler Email(AsyncServiceScope scope, IEmailSender email, INotificationDocumentSource source) => new(
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), scope.ServiceProvider.GetRequiredService<NotificationTemplateRenderer>(),
        [source], email, TimeProvider.System, NullLogger<EmailDeliveryHandler>.Instance);

    private static WhatsAppDeliveryHandler WhatsApp(AsyncServiceScope scope, IWhatsAppSender whatsApp, INotificationDocumentSource source) => new(
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), scope.ServiceProvider.GetRequiredService<NotificationTemplateRenderer>(),
        [source], whatsApp, TimeProvider.System, NullLogger<WhatsAppDeliveryHandler>.Instance);

    private static FakeSource Source(string kind, Func<Guid, NotificationFile?> make) => new(kind, make);

    private sealed class FakeSource(string kind, Func<Guid, NotificationFile?> make) : INotificationDocumentSource
    {
        public string Kind => kind;

        public Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(make(id));
    }

    private sealed class FakeEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<SendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Ok("email-1"));
        }
    }

    private sealed class FakeWhatsApp : IWhatsAppSender
    {
        public string Provider => "fake";

        public List<WhatsAppTemplateMessage> Sent { get; } = [];

        public Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Ok("wamid.1"));
        }

        public Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
