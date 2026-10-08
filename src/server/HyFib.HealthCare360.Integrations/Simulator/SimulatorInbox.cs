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

    /// <summary>
    /// Returns part of a simulated payment and keeps the refund as its own transaction (so it can be settled and looked
    /// up later). A refund id already kept is not refunded again (a repeated request). Returns why it was refused, or
    /// null.
    /// </summary>
    public async Task<string?> RefundAsync(string paymentId, decimal amount, string refundId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await RefundExistsAsync(refundId, cancellationToken))
        {
            return null;
        }

        var payment = await db.Set<SimulatorTransaction>()
            .FirstOrDefaultAsync(t => t.PaymentId == paymentId && (t.Kind == SimulatorTransactionKind.PaymentOrder || t.Kind == SimulatorTransactionKind.PaymentLink),
                cancellationToken);
        if (payment is null || amount <= 0 || amount > payment.Amount - payment.RefundedAmount)
        {
            return "The refund exceeds the simulated payment.";
        }

        var now = time.GetUtcNow();
        payment.RefundedAmount += amount;
        if (payment.RefundedAmount >= payment.Amount)
        {
            payment.Status = SimulatorTransactionStatus.Refunded;
        }

        db.Set<SimulatorTransaction>().Add(new SimulatorTransaction
        {
            CreatedAt = now,
            CompletedAt = now,
            FacilityId = payment.FacilityId,
            Kind = SimulatorTransactionKind.PaymentRefund,
            ExternalId = refundId,
            Amount = amount,
            Currency = payment.Currency,
            Reference = payment.PaymentId,
            Status = SimulatorTransactionStatus.Refunded,
            Description = "Refund",
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same request arrived twice at once: the other one made the refund.
            if (!await RefundExistsAsync(refundId, cancellationToken))
            {
                throw;
            }
        }

        return null;
    }

    private async Task<bool> RefundExistsAsync(string refundId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Set<SimulatorTransaction>().AnyAsync(
            t => t.ExternalId == refundId && t.Kind == SimulatorTransactionKind.PaymentRefund, cancellationToken);
    }

    public async Task<SimulatorTransaction?> FindRefundAsync(string refundId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Set<SimulatorTransaction>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.ExternalId == refundId && t.Kind == SimulatorTransactionKind.PaymentRefund, cancellationToken);
    }

    /// <summary>
    /// A settlement run: every captured payment and refund made before <paramref name="coverBefore"/> that no earlier run
    /// took is given to <paramref name="settlementId"/>. The update is conditional, so two runs at once never settle a
    /// transaction twice.
    /// </summary>
    public async Task SettleAsync(DateTimeOffset settleAt, DateTimeOffset coverBefore, string settlementId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var due = await db.Set<SimulatorTransaction>()
            .Where(t => t.SettlementId == null && t.CreatedAt < coverBefore
                        && (t.Kind == SimulatorTransactionKind.PaymentRefund
                            || ((t.Kind == SimulatorTransactionKind.PaymentOrder || t.Kind == SimulatorTransactionKind.PaymentLink)
                                && t.PaymentId != null
                                && (t.Status == SimulatorTransactionStatus.Captured || t.Status == SimulatorTransactionStatus.Refunded))))
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Id)
            .Take(5_000)
            .ToListAsync(cancellationToken);
        if (due.Count > 0)
        {
            await db.Set<SimulatorTransaction>()
                .Where(t => due.Contains(t.Id) && t.SettlementId == null)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.SettlementId, settlementId).SetProperty(t => t.SettledAt, settleAt), cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SimulatorTransaction>> SettledBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Set<SimulatorTransaction>().AsNoTracking()
            .Where(t => t.SettlementId != null && t.SettledAt >= from && t.SettledAt < to)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The punches made on a training attendance terminal since <paramref name="since"/>, oldest first.</summary>
    public async Task<IReadOnlyList<SimulatorTransaction>> TerminalPunchesAsync(
        Guid facilityId, string serialNumber, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Set<SimulatorTransaction>().AsNoTracking()
            .Where(t => t.Kind == SimulatorTransactionKind.TerminalPunch && t.FacilityId == facilityId && t.TerminalId == serialNumber
                        && t.CreatedAt >= since)
            .OrderBy(t => t.CreatedAt)
            .Take(20_000)
            .ToListAsync(cancellationToken);
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
