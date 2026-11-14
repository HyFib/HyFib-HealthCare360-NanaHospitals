using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Integrations.Laboratory.Orders;

/// <summary>
/// Takes Diagnostics' <c>lab.analyser.order</c> outbox messages into the per-analyser order queue, in the same
/// transaction that marks the message delivered. The queue keeps each order until its analyser is connected. Local: an
/// order for the lab's analysers never waits behind the internet.
/// </summary>
internal sealed class LabOrderOutboxHandler(LabOrderQueue queue) : IOutboxHandler
{
    public string Type => InstrumentOrderRequest.OutboxType;

    public OutboxLane Lane => OutboxLane.Local;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        queue.AddAsync(message, message.Read<InstrumentOrderRequest>(), cancellationToken);
}
