using System.Text.Json;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A message withdrawn before it went, as the alert of a server's network cable lost is when the cable is back first
/// (DED-06): through the real notification log and SMS delivery handler. Theni's server reaches the SMS gateway over its
/// one cable, so the alert of its loss fails and waits in the outbox while the cable is out; once the cable is back the
/// loss is withdrawn, the outbox's next try sends nothing, and IT's phone gets the return alone, which says both.
/// </summary>
public sealed class WithdrawnNotificationTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_withdrawn_before_it_went_is_never_sent_and_one_already_sent_is_left_as_it_is()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var queued = await AlertAsync(db, NotificationStatus.Queued);
        var failed = await AlertAsync(db, NotificationStatus.Failed, "The SMS gateway could not be reached.");
        var sent = await AlertAsync(db, NotificationStatus.Sent);
        var delivered = await AlertAsync(db, NotificationStatus.Delivered);

        var withdrawn = await NotificationLogs.WithdrawAsync(
            db, [queued.LogId, failed.LogId, sent.LogId, delivered.LogId], WifiMonitor.WithdrawnBecauseBack, Ct);
        await db.SaveChangesAsync(Ct);

        withdrawn.ShouldBe(2, "the one queued and the one that failed and waits to be tried again");
        (await StatusAsync(queued.LogId)).ShouldBe((NotificationStatus.Suppressed, WifiMonitor.WithdrawnBecauseBack));
        (await StatusAsync(failed.LogId)).ShouldBe((NotificationStatus.Suppressed, WifiMonitor.WithdrawnBecauseBack));
        (await StatusAsync(sent.LogId)).ShouldBe((NotificationStatus.Sent, null));
        (await StatusAsync(delivered.LogId)).ShouldBe((NotificationStatus.Delivered, null));

        // The outbox tries them again: nothing goes, and nothing fails, so they are not tried again.
        var sms = new FakeSms();
        await Handler(scope, sms).HandleAsync(Message(queued), Ct);
        await Handler(scope, sms).HandleAsync(Message(failed), Ct);

        sms.Sent.ShouldBeEmpty();
        (await StatusAsync(failed.LogId)).ShouldBe((NotificationStatus.Suppressed, WifiMonitor.WithdrawnBecauseBack));
    }

    [Fact]
    public async Task A_message_not_withdrawn_goes_as_before()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var back = await AlertAsync(db, NotificationStatus.Queued,
            message: "Server Node's network cable is connected again ('Ethernet', 1 Gbps): workstations can reach HC360 again. Lost at 09:30, back at 09:31.");
        var sms = new FakeSms();

        await Handler(scope, sms).HandleAsync(Message(back), Ct);

        sms.Sent.ShouldHaveSingleItem().Text.ShouldBe("HC360 server alert (HC360-SRV): Server Node's network cable is connected again ('Ethernet', 1 Gbps): "
            + "workstations can reach HC360 again. Lost at 09:30, back at 09:31.");
        (await StatusAsync(back.LogId)).Status.ShouldBe(NotificationStatus.Sent);
    }

    /// <summary>An alert to IT's on-call phone as the link monitor queues it, its log in <paramref name="status"/>.</summary>
    private static async Task<NotificationDelivery> AlertAsync(
        HospitalDbContext db, NotificationStatus status, string? error = null,
        string message = "Server Node lost its network cable ('Ethernet'): the cable is out or its switch is off. Workstations cannot reach HC360 until it is back.")
    {
        var log = new NotificationLog
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Channel = NotificationChannel.Sms,
            TemplateKey = "system.alert",
            Status = status,
            Error = error,
        };
        db.Add(log);
        await db.SaveChangesAsync(Ct);
        return new NotificationDelivery(log.Id, "system.alert", NotificationChannel.Sms, "+919800000001", "IT on-call", "en",
            new Dictionary<string, string> { ["server"] = "HC360-SRV", ["message"] = message }, null);
    }

    private async Task<(NotificationStatus Status, string? Error)> StatusAsync(Guid logId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var log = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking()
            .SingleAsync(l => l.Id == logId, Ct);
        return (log.Status, log.Error);
    }

    private static OutboxMessage Message(NotificationDelivery delivery) =>
        new() { Type = NotificationService.SmsType, Payload = JsonSerializer.Serialize(delivery, Json) };

    private static SmsDeliveryHandler Handler(AsyncServiceScope scope, ISmsSender sms) => new(
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), scope.ServiceProvider.GetRequiredService<NotificationTemplateRenderer>(),
        [], sms, TimeProvider.System, NullLogger<SmsDeliveryHandler>.Instance);

    private sealed class FakeSms : ISmsSender
    {
        public List<SmsMessage> Sent { get; } = [];

        public string Provider => "fake";

        public Task<SendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Ok("sms-" + Sent.Count));
        }
    }
}
