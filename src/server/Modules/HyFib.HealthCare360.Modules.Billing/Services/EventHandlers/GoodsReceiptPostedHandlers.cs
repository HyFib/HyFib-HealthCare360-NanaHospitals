using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

// A goods receipt is a purchase in the Tally day book of the day its goods were received (N11), which may have gone to
// Tally before the receipt was posted into stock. Supply chain posts nothing to Billing: the handler only queues an outbox
// message in Supply chain's transaction, and TallyCorrections puts the day back in the Tally queue when it is delivered.

/// <summary>A goods receipt of an earlier day posted into stock: its day may need to go to Tally again.</summary>
internal sealed class GoodsReceiptPostedHandler(IOutbox outbox, TimeProvider time) : IDomainEventHandler<GoodsReceiptPosted>
{
    public Task HandleAsync(GoodsReceiptPosted posted, CancellationToken cancellationToken)
    {
        // A receipt of today joins a day book the nightly post has yet to send.
        if (posted.ReceivedOn < BillingTime.Today(time))
        {
            outbox.Enqueue(TallyCorrections.LatePurchaseMessage, new LatePurchase(posted.FacilityId, posted.ReceivedOn, posted.Number), posted.FacilityId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Delivers <c>billing.tally.late-purchase</c> messages (retried with back-off on failure).</summary>
internal sealed class LatePurchaseOutboxHandler(TallyCorrections corrections) : IOutboxHandler
{
    public string Type => TallyCorrections.LatePurchaseMessage;

    /// <summary>Work in the database: the day is queued again, and posting it to Tally is a message of its own.</summary>
    public OutboxLane Lane => OutboxLane.Local;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        corrections.RepostForLatePurchaseAsync(message.Read<LatePurchase>(), cancellationToken);
}
