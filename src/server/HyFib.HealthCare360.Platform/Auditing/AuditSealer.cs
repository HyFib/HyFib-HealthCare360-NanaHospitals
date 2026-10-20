using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Auditing;

public sealed record AuditVerification(bool Valid, long CheckedCount, long? FirstInvalidChainIndex, long UnsealedCount, string? Message);

/// <summary>Builds and verifies the tamper-evident SHA-256 chain over audit events (ADR-010).</summary>
public sealed class AuditChainService(HospitalDbContext db)
{
    private const long AdvisoryLockKey = 4_247_360;
    private const int SealBatchSize = 500;
    private const int VerifyBatchSize = 5000;

    /// <summary>Seals unsealed events in insertion order. A PostgreSQL advisory lock keeps a single sealer across nodes.</summary>
    public async Task<int> SealPendingAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey}) AS \"Value\"").SingleAsync(cancellationToken);
        if (!locked)
        {
            return 0;
        }

        var pending = await db.Set<AuditEvent>()
            .Where(e => e.Hash == null)
            .OrderBy(e => e.Id)
            .Take(SealBatchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        // Only with something to seal, which is not every five seconds: the end of the chain may be in any month, so finding
        // it plans a look into every month's partition (some 40 ms with ten years of them).
        var last = await db.Set<AuditEvent>()
            .Where(e => e.ChainIndex != null)
            .OrderByDescending(e => e.ChainIndex)
            .Select(e => new { e.ChainIndex, e.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        var index = last?.ChainIndex ?? 0;
        var previous = last?.Hash;
        foreach (var auditEvent in pending)
        {
            index++;
            auditEvent.ChainIndex = index;
            auditEvent.PreviousHash = previous;
            auditEvent.Hash = AuditEvent.ComputeHash(previous, auditEvent.CanonicalContent());
            previous = auditEvent.Hash;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending.Count;
    }

    /// <summary>Recomputes the whole chain; any edited, deleted or reordered row breaks verification.</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        long checkedCount = 0;
        long expectedIndex = 1;
        string? previous = null;

        while (true)
        {
            var batch = await db.Set<AuditEvent>().AsNoTracking()
                .Where(e => e.ChainIndex >= expectedIndex)
                .OrderBy(e => e.ChainIndex)
                .Take(VerifyBatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var auditEvent in batch)
            {
                if (auditEvent.ChainIndex != expectedIndex
                    || !string.Equals(auditEvent.PreviousHash, previous, StringComparison.Ordinal)
                    || !string.Equals(auditEvent.Hash, AuditEvent.ComputeHash(previous, auditEvent.CanonicalContent()), StringComparison.Ordinal))
                {
                    return new AuditVerification(false, checkedCount, expectedIndex, 0,
                        $"The audit chain is broken at position {expectedIndex}. Records may have been altered or removed.");
                }

                previous = auditEvent.Hash;
                expectedIndex++;
                checkedCount++;
            }
        }

        // A seal without a chain position would hide the row from the walk above: treat it as tampering.
        var outsideChain = await db.Set<AuditEvent>().LongCountAsync(e => e.Hash != null && e.ChainIndex == null, cancellationToken);
        if (outsideChain > 0)
        {
            return new AuditVerification(false, checkedCount, null, 0,
                $"{outsideChain:N0} audit record(s) carry a seal outside the chain. Records may have been altered.");
        }

        var unsealed = await db.Set<AuditEvent>().LongCountAsync(e => e.Hash == null, cancellationToken);
        return new AuditVerification(true, checkedCount, null, unsealed, $"Verified {checkedCount:N0} audit records.");
    }
}

/// <summary>Seals new audit events every few seconds.</summary>
internal sealed class AuditSealer(IServiceScopeFactory scopeFactory, TimeProvider time, IOptions<PlatformOptions> options, ILogger<AuditSealer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                int sealedCount;
                do
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    sealedCount = await scope.ServiceProvider.GetRequiredService<AuditChainService>().SealPendingAsync(stoppingToken);
                }
                while (sealedCount > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit sealing failed");
            }
        }
    }
}
