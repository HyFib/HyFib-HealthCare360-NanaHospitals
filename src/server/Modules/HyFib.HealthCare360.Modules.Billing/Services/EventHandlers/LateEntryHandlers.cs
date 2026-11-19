using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

// A bill or a receipt written on paper during a downtime, and a pharmacy sale written on paper, are dated at the time on
// the paper, so each is in the day book of that day (docs/modules/downtime.md), which may have gone to Tally before the
// paper was entered. Each is saved with an outbox message in its own transaction, and TallyCorrections puts the day back
// in the Tally queue when the message is delivered, if the day went already. An entry dated today needs none: tonight's
// post sends it.

/// <summary>Queues a look at an earlier day's Tally day book, in the transaction of the entry that is dated in it.</summary>
public sealed class LateEntryQueue(IOutbox outbox, TimeProvider time)
{
    /// <summary>The entry is dated <paramref name="at"/>: its day is looked at again when that is before today in India.</summary>
    public void Dated(Guid facilityId, DateTimeOffset at, string source, string? reference) =>
        Dated(facilityId, BillingTime.LocalDate(at), source, reference);

    public void Dated(Guid facilityId, DateOnly date, string source, string? reference)
    {
        if (date < BillingTime.Today(time))
        {
            outbox.Enqueue(TallyCorrections.LateEntryMessage, new LateEntry(facilityId, date, source, reference), facilityId);
        }
    }
}

/// <summary>A record of another module that is dated in an earlier day (a pharmacy sale entered from paper).</summary>
internal sealed class DayBookEntryBackdatedHandler(LateEntryQueue queue) : IDomainEventHandler<DayBookEntryBackdated>
{
    public Task HandleAsync(DayBookEntryBackdated backdated, CancellationToken cancellationToken)
    {
        queue.Dated(backdated.FacilityId, backdated.Date, backdated.Source, backdated.Reference);
        return Task.CompletedTask;
    }
}

/// <summary>Delivers <c>billing.tally.late-entry</c> messages (retried with back-off on failure).</summary>
internal sealed class LateEntryOutboxHandler(TallyCorrections corrections) : IOutboxHandler
{
    public string Type => TallyCorrections.LateEntryMessage;

    /// <summary>Work in the database: the day is queued again, and posting it to Tally is a message of its own.</summary>
    public OutboxLane Lane => OutboxLane.Local;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var entry = message.Read<LateEntry>();
        return corrections.RepostIfPostedAsync(entry.FacilityId, entry.Date, cancellationToken);
    }
}
