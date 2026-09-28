using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>Outbox payload: a queue entry changed inside another module's transaction (a prescription was signed or cancelled).</summary>
public sealed record PharmacyQueueChanged(Guid FacilityId, Guid EntryId, string Status);

/// <summary>
/// Real-time refresh for pharmacy screens on <see cref="Topics.Pharmacy"/> (ids and status only — clients refetch
/// with their own permissions). Changes made by pharmacy endpoints publish after commit; changes made inside
/// another module's transaction (event handlers) go through the outbox so screens never refetch uncommitted data.
/// </summary>
public sealed class PharmacyRealtime(IRealtimePublisher realtime)
{
    public const string QueueChangedEvent = "pharmacy.queue.changed";
    public const string WardIndentChangedEvent = "pharmacy.ward_indent.changed";
    public const string RefillChangedEvent = "pharmacy.refill.changed";
    public const string QueueChangedOutboxType = "pharmacy.queue.changed";

    public Task QueueChangedAsync(Guid facilityId, Guid entryId, QueueEntryStatus status, CancellationToken ct) =>
        realtime.PublishAsync(Topics.Pharmacy(facilityId), QueueChangedEvent, new { entryId, status }, ct);

    public Task WardIndentChangedAsync(Guid facilityId, Guid indentId, WardIndentStatus status, CancellationToken ct) =>
        realtime.PublishAsync(Topics.Pharmacy(facilityId), WardIndentChangedEvent, new { indentId, status }, ct);

    public Task RefillChangedAsync(Guid facilityId, Guid requestId, RefillStatus status, CancellationToken ct) =>
        realtime.PublishAsync(Topics.Pharmacy(facilityId), RefillChangedEvent, new { requestId, status }, ct);

    /// <summary>Queues the notification in the caller's unit of work (published after it commits).</summary>
    public static void QueueChangedAfterCommit(IOutbox outbox, PharmacyQueueEntry entry) =>
        outbox.Enqueue(QueueChangedOutboxType, new PharmacyQueueChanged(entry.FacilityId, entry.Id, entry.Status.ToString()), entry.FacilityId,
            expiresIn: TimeSpan.FromMinutes(10));
}

/// <summary>Publishes queue changes that were enqueued by domain-event handlers once their transaction has committed.</summary>
internal sealed class PharmacyQueueChangedHandler(IRealtimePublisher realtime) : IOutboxHandler
{
    public string Type => PharmacyRealtime.QueueChangedOutboxType;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var change = message.Read<PharmacyQueueChanged>();
        return realtime.PublishAsync(Topics.Pharmacy(change.FacilityId), PharmacyRealtime.QueueChangedEvent,
            new { entryId = change.EntryId, status = change.Status }, cancellationToken);
    }
}
