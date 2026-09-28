using HyFib.HealthCare360.Integrations.Domain;

namespace HyFib.HealthCare360.Integrations.Simulator;

/// <summary>
/// Persistence for the simulator providers. It uses its own DbContext: callers such as the outbox dispatcher hold an
/// open transaction on the scoped context, and a training message must neither ride on nor roll back with their work.
/// </summary>
internal sealed class SimulatorInbox(IDbContextFactory<HospitalDbContext> contexts, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<string> RecordAsync(
        SimulatorChannel channel, string? recipient, string? subject, string body, string? details, string? reference, CancellationToken cancellationToken)
    {
        var providerMessageId = "SIM-" + Guid.CreateVersion7().ToString("N").ToUpperInvariant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Set<SimulatorMessage>().Add(new SimulatorMessage
        {
            CreatedAt = time.GetUtcNow(),
            FacilityId = currentUser.FacilityId,
            Channel = channel,
            Recipient = IntegrationTrace.Truncate(recipient, 256),
            Subject = IntegrationTrace.Truncate(subject, 300),
            Body = IntegrationTrace.Truncate(body, 8000) ?? string.Empty,
            Details = IntegrationTrace.Truncate(details, 2000),
            Reference = IntegrationTrace.Truncate(reference, 128),
            ProviderMessageId = providerMessageId,
        });
        await db.SaveChangesAsync(cancellationToken);
        return providerMessageId;
    }

    public async Task<SimulatorTransaction> AddTransactionAsync(SimulatorTransaction transaction, CancellationToken cancellationToken)
    {
        transaction.CreatedAt = time.GetUtcNow();
        transaction.FacilityId ??= currentUser.FacilityId;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Set<SimulatorTransaction>().Add(transaction);
        await db.SaveChangesAsync(cancellationToken);
        return transaction;
    }

    /// <summary>Loads a transaction by gateway/terminal id or payment id and lets <paramref name="update"/> change it.</summary>
    public async Task<SimulatorTransaction?> UpdateTransactionAsync(
        string id, Func<SimulatorTransaction, DateTimeOffset, bool> update, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var transaction = await db.Set<SimulatorTransaction>().FirstOrDefaultAsync(t => t.ExternalId == id || t.PaymentId == id, cancellationToken);
        if (transaction is not null && update(transaction, time.GetUtcNow()))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return transaction;
    }
}
