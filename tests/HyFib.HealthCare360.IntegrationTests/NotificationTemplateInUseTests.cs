using System.Text.Json;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// "In use" on a notification template (Administration › Notification templates): a version not in use gives way to the
/// next one that fits, and when no version that fits is in use, the message is not sent on that channel: skipped with
/// the reason, never retried, and its document never made. The delivery handlers run against the test server's database
/// with fake senders and templates of each test alone (unique keys), so nothing else the server sends is affected.
/// </summary>
public sealed class NotificationTemplateInUseTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_whose_only_version_is_not_in_use_is_skipped_without_its_document_and_goes_once_switched_on()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = Key();
        var template = await AddAsync(scope, key, NotificationChannel.WhatsApp, null, "en", "Dear {{name}}, your report is ready.", inUse: false, documentHeader: true);
        var whatsApp = new FakeWhatsApp();
        var made = 0;
        var source = new FakeSource("test." + key, () => made++);
        var (delivery, logId) = await DeliveryAsync(scope, key, NotificationChannel.WhatsApp, Number(), null, "en", new NotificationDocument("test." + key, Guid.NewGuid()));

        // No exception, so the outbox does not try it again.
        await WhatsApp(scope, whatsApp, source).HandleAsync(Message(NotificationService.WhatsAppType, delivery), Ct);

        whatsApp.Sent.ShouldBeEmpty();
        made.ShouldBe(0, "Nothing is made for a message that is not sent.");
        var log = await LogAsync(scope, logId);
        log.Status.ShouldBe(NotificationStatus.Skipped);
        log.Error.ShouldBe(TemplateSwitch.NotInUseReason(NotificationChannel.WhatsApp));
        log.Error.ShouldNotBeNull().ShouldContain("WhatsApp template is set to \"Not in use\"");

        // Switched on again (once Meta approves it, say), the next message goes, with its document.
        await SwitchAsync(scope, template.Id, inUse: true);
        var (again, againLogId) = await DeliveryAsync(scope, key, NotificationChannel.WhatsApp, Number(), null, "en", new NotificationDocument("test." + key, Guid.NewGuid()));
        await WhatsApp(scope, whatsApp, source).HandleAsync(Message(NotificationService.WhatsAppType, again), Ct);

        whatsApp.Sent.ShouldHaveSingleItem().HeaderDocument.ShouldNotBeNull();
        made.ShouldBe(1);
        (await LogAsync(scope, againLogId)).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task A_facility_version_not_in_use_gives_way_to_the_version_for_all_facilities()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = Key();
        var facilityId = await FacilityAsync(scope);
        await AddAsync(scope, key, NotificationChannel.Sms, null, "en", "All facilities: {{reference}}", inUse: true);
        await AddAsync(scope, key, NotificationChannel.Sms, facilityId, "en", "This facility: {{reference}}", inUse: false);
        var sms = new FakeSms();
        var (delivery, logId) = await DeliveryAsync(scope, key, NotificationChannel.Sms, Number(), facilityId, "en");

        await Sms(scope, sms).HandleAsync(Message(NotificationService.SmsType, delivery), Ct);

        sms.Sent.ShouldHaveSingleItem().Text.ShouldBe("All facilities: NH-42");
        (await LogAsync(scope, logId)).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task A_language_version_not_in_use_gives_way_to_english_and_with_both_off_nothing_goes()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = Key();
        var english = await AddAsync(scope, key, NotificationChannel.Sms, null, "en", "Reference {{reference}}", inUse: true);
        await AddAsync(scope, key, NotificationChannel.Sms, null, "ta", "குறிப்பு {{reference}}", inUse: false);
        var sms = new FakeSms();

        var (tamilReader, _) = await DeliveryAsync(scope, key, NotificationChannel.Sms, Number(), null, "ta");
        await Sms(scope, sms).HandleAsync(Message(NotificationService.SmsType, tamilReader), Ct);
        sms.Sent.ShouldHaveSingleItem().Text.ShouldBe("Reference NH-42");

        // With the English version switched off too, no version the patient reads is in use.
        await SwitchAsync(scope, english.Id, inUse: false);
        var (later, laterLogId) = await DeliveryAsync(scope, key, NotificationChannel.Sms, Number(), null, "ta");
        await Sms(scope, sms).HandleAsync(Message(NotificationService.SmsType, later), Ct);
        sms.Sent.Count.ShouldBe(1);
        var log = await LogAsync(scope, laterLogId);
        (log.Status, log.Error).ShouldBe((NotificationStatus.Skipped, TemplateSwitch.NotInUseReason(NotificationChannel.Sms)));
    }

    [Fact]
    public async Task A_template_switched_off_on_one_channel_still_goes_on_the_others()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = Key();
        await AddAsync(scope, key, NotificationChannel.Sms, null, "en", "Reference {{reference}}", inUse: true);
        await AddAsync(scope, key, NotificationChannel.WhatsApp, null, "en", "Reference {{reference}}", inUse: false);
        var sms = new FakeSms();
        var whatsApp = new FakeWhatsApp();
        var (bySms, smsLogId) = await DeliveryAsync(scope, key, NotificationChannel.Sms, Number(), null, "en");
        var (byWhatsApp, whatsAppLogId) = await DeliveryAsync(scope, key, NotificationChannel.WhatsApp, Number(), null, "en");

        await Sms(scope, sms).HandleAsync(Message(NotificationService.SmsType, bySms), Ct);
        await WhatsApp(scope, whatsApp, new FakeSource("none", () => { })).HandleAsync(Message(NotificationService.WhatsAppType, byWhatsApp), Ct);

        sms.Sent.ShouldHaveSingleItem();
        whatsApp.Sent.ShouldBeEmpty();
        (await LogAsync(scope, smsLogId)).Status.ShouldBe(NotificationStatus.Sent);
        (await LogAsync(scope, whatsAppLogId)).Status.ShouldBe(NotificationStatus.Skipped);
    }

    private static string Key() => "test.inuse_" + Guid.NewGuid().ToString("N")[..10];

    // A number of each test's own: no STOP another test recorded applies to it.
    private static string Number() => "+919" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static async Task<Guid> FacilityAsync(AsyncServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Facility>().AsNoTracking().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstAsync(Ct);

    private static async Task<NotificationTemplate> AddAsync(
        AsyncServiceScope scope, string key, NotificationChannel channel, Guid? facilityId, string language, string body, bool inUse, bool documentHeader = false)
    {
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var template = new NotificationTemplate
        {
            Key = key,
            Channel = channel,
            FacilityId = facilityId,
            Language = language,
            Body = body,
            IsActive = inUse,
            WhatsAppDocumentHeader = documentHeader,
        };
        db.Add(template);
        await db.SaveChangesAsync(Ct);
        return template;
    }

    private static async Task SwitchAsync(AsyncServiceScope scope, Guid templateId, bool inUse)
    {
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var template = await db.Set<NotificationTemplate>().SingleAsync(t => t.Id == templateId, Ct);
        template.IsActive = inUse;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<(NotificationDelivery Delivery, Guid LogId)> DeliveryAsync(
        AsyncServiceScope scope, string key, NotificationChannel channel, string to, Guid? facilityId, string language, NotificationDocument? document = null)
    {
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var log = new NotificationLog { CreatedAt = DateTimeOffset.UtcNow, Channel = channel, TemplateKey = key, FacilityId = facilityId, Status = NotificationStatus.Queued };
        db.Add(log);
        await db.SaveChangesAsync(Ct);
        var delivery = new NotificationDelivery(log.Id, key, channel, to, "Asha", language, new Dictionary<string, string> { ["reference"] = "NH-42" }, facilityId, document);
        return (delivery, log.Id);
    }

    private static async Task<NotificationLog> LogAsync(AsyncServiceScope scope, Guid id) =>
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking().SingleAsync(l => l.Id == id, Ct);

    private static OutboxMessage Message(string type, NotificationDelivery delivery) =>
        new() { Type = type, Payload = JsonSerializer.Serialize(delivery, Json) };

    private static SmsDeliveryHandler Sms(AsyncServiceScope scope, ISmsSender sms) => new(
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), scope.ServiceProvider.GetRequiredService<NotificationTemplateRenderer>(),
        [], sms, TimeProvider.System, NullLogger<SmsDeliveryHandler>.Instance);

    private static WhatsAppDeliveryHandler WhatsApp(AsyncServiceScope scope, IWhatsAppSender whatsApp, INotificationDocumentSource source) => new(
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), scope.ServiceProvider.GetRequiredService<NotificationTemplateRenderer>(),
        [source], whatsApp, TimeProvider.System, NullLogger<WhatsAppDeliveryHandler>.Instance);

    private sealed class FakeSource(string kind, Action made) : INotificationDocumentSource
    {
        public string Kind => kind;

        public Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
        {
            made();
            return Task.FromResult<NotificationFile?>(new NotificationFile("report.pdf", [0x25, 0x50, 0x44, 0x46], "application/pdf"));
        }
    }

    private sealed class FakeSms : ISmsSender
    {
        public string Provider => "fake";

        public List<SmsMessage> Sent { get; } = [];

        public Task<SendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Ok("sms-" + Sent.Count));
        }
    }

    private sealed class FakeWhatsApp : IWhatsAppSender
    {
        public string Provider => "fake";

        public List<WhatsAppTemplateMessage> Sent { get; } = [];

        public Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Ok("wamid." + Sent.Count));
        }

        public Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
