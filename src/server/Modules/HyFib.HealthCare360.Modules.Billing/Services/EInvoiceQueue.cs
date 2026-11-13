using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Outbox payload: get the IRN of one document of the e-invoice register, by its <see cref="IEInvoiceSource.Source"/> and id.</summary>
public sealed record EInvoiceGenerateMessage(string Source, Guid Id);

/// <summary>
/// Billing's <see cref="IEInvoiceQueue"/>: a document of a day e-invoicing applies to is queued in the caller's transaction,
/// for <see cref="EInvoiceGenerateHandler"/> to ask the portal for its IRN once that transaction commits.
/// </summary>
internal sealed class EInvoiceQueue(IOutbox outbox, ISettingsService settings) : IEInvoiceQueue
{
    public async Task QueueAsync(string source, Guid id, Guid facilityId, DateOnly date, CancellationToken cancellationToken)
    {
        if (await EInvoiceService.ApplicableFromAsync(settings, facilityId, cancellationToken) is { } from && date >= from)
        {
            outbox.Enqueue(EInvoiceGenerateHandler.MessageType, new EInvoiceGenerateMessage(source, id), facilityId);
        }
    }
}

/// <summary>
/// Delivers <c>billing.einvoice.generate</c> (<see cref="EInvoiceService.GenerateQueuedAsync"/>) in a scope of its own: the
/// IRN is saved as soon as the portal gives it, and a conflict on the register (another request recorded the IRN at the
/// same moment) fails this message alone, not the outbox's whole batch. What can change on its own is tried again with the
/// outbox's back-off (an exception says why); the rest ends here.
/// </summary>
internal sealed class EInvoiceGenerateHandler(IServiceScopeFactory scopes) : IOutboxHandler
{
    public const string MessageType = "billing.einvoice.generate";

    public string Type => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var queued = message.Read<EInvoiceGenerateMessage>();
        (EInvoiceAutoOutcome Outcome, string Reason) result;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            result = await scope.ServiceProvider.GetRequiredService<EInvoiceService>().GenerateQueuedAsync(queued.Source, queued.Id, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException($"The IRN of {queued.Source} {queued.Id} was being recorded by another request at the same moment; it is looked at again.", ex);
        }

        if (result.Outcome == EInvoiceAutoOutcome.Retry)
        {
            throw new InvalidOperationException(result.Reason);
        }
    }
}

/// <summary>A bill finalized with GST, to a payer registered for GST, is queued for its IRN in the finalizing transaction.</summary>
internal sealed class QueueBillEInvoice(HospitalDbContext db, IEInvoiceQueue queue) : IDomainEventHandler<BillFinalized>
{
    public async Task HandleAsync(BillFinalized finalized, CancellationToken cancellationToken)
    {
        if (db.Set<Bill>().Local.FirstOrDefault(b => b.Id == finalized.BillId) is { TaxAmount: not 0m, PayerId: { } payerId, FinalizedAt: { } at } bill
            && await db.Set<Payer>().AnyAsync(p => p.Id == payerId && p.Gstin != null, cancellationToken))
        {
            await queue.QueueAsync(BillEInvoiceSource.Key, bill.Id, bill.FacilityId, BillingTime.LocalDate(at), cancellationToken);
        }
    }
}
