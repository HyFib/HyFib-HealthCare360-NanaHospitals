using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <param name="AnchorsChecked">How many anchors outside the database (<see cref="AuditAnchors"/>) the chain was checked against.</param>
/// <param name="LastAnchor">The latest of them, if any.</param>
public sealed record AuditVerification(bool Valid, long CheckedCount, long? FirstInvalidChainIndex, long UnsealedCount, string? Message)
{
    public int AnchorsChecked { get; init; }
    public AuditAnchor? LastAnchor { get; init; }
}

/// <summary>
/// Builds and verifies the tamper-evident chain over audit events (ADR-010): each event's SHA-256 hash covers the one before
/// it, and its seal (<see cref="AuditEvent.Mac"/>) is an HMAC of that hash under a key held outside the database, so a
/// chain made again over changed rows does not verify. Anchors written outside the database show where the chain ended.
/// </summary>
public sealed class AuditChainService(HospitalDbContext db, AuditSealKey key, AuditAnchors anchors, TimeProvider time)
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
            auditEvent.Mac = key.Seal(auditEvent.Hash);
            previous = auditEvent.Hash;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending.Count;
    }

    /// <summary>
    /// Appends the chain's end to the anchors when the last anchor is older than <paramref name="interval"/> and the chain
    /// has grown since. Returns the anchor written, or null.
    /// </summary>
    public async Task<AuditAnchor?> AnchorIfDueAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var last = anchors.Last();
        var now = time.GetUtcNow();
        if (last is not null && now - last.AnchoredAt < interval)
        {
            return null;
        }

        var end = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.ChainIndex != null)
            .OrderByDescending(e => e.ChainIndex)
            .Select(e => new { e.ChainIndex, e.Hash, e.Mac })
            .FirstOrDefaultAsync(cancellationToken);
        if (end?.ChainIndex is not { } index || index <= (last?.ChainIndex ?? 0))
        {
            return null;
        }

        var anchor = new AuditAnchor(index, end.Hash!, end.Mac, now);
        anchors.Append(anchor);
        return anchor;
    }

    /// <summary>
    /// Recomputes the whole chain: any edited, deleted or reordered row breaks it. Each seal must be this server's key's
    /// seal of its hash, and once events carry seals every later one must (none may be dropped). The chain must then hold
    /// every anchor written outside the database, with the same hash: a chain cut short, or made again from an earlier
    /// point, does not.
    /// </summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        long checkedCount = 0;
        long expectedIndex = 1;
        string? previous = null;
        long? firstSealed = null;
        var (anchorList, unreadableAnchors) = anchors.Read();
        var anchored = anchorList.GroupBy(a => a.ChainIndex).ToDictionary(g => g.Key, g => g.Select(a => a.Hash).ToHashSet(StringComparer.Ordinal));
        var lastAnchor = anchorList.LastOrDefault();
        AuditVerification Broken(long? at, string message) =>
            new(false, checkedCount, at, 0, message) { AnchorsChecked = anchorList.Count, LastAnchor = lastAnchor };

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
                    || !auditEvent.IsSealedAfter(previous))
                {
                    return Broken(expectedIndex, $"The audit chain is broken at position {expectedIndex}. Records may have been altered or removed.");
                }

                if (auditEvent.Mac is null)
                {
                    if (firstSealed is not null)
                    {
                        return Broken(expectedIndex, $"The audit record at position {expectedIndex} has lost its seal. Records may have been altered.");
                    }
                }
                else if (!key.Sealed(auditEvent.Hash!, auditEvent.Mac))
                {
                    return Broken(expectedIndex, firstSealed is null
                        ? $"The audit records from position {expectedIndex} were sealed with a key this server does not hold, or have been altered. " +
                          "If this database was restored, restore the keys from the same backup."
                        : $"The seal of the audit record at position {expectedIndex} does not match. Records may have been altered.");
                }
                else
                {
                    firstSealed ??= expectedIndex;
                }

                // Every anchor of the position must be this record: one that is not shows the chain was made again since.
                if (anchored.TryGetValue(expectedIndex, out var hashes) && hashes.Any(h => !string.Equals(h, auditEvent.Hash, StringComparison.Ordinal)))
                {
                    return Broken(expectedIndex, $"The audit record at position {expectedIndex} is not the one anchored outside the database. The chain has been made again.");
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
            return Broken(null, $"{outsideChain:N0} audit record(s) carry a seal outside the chain. Records may have been altered.");
        }

        // An anchor past the end: records anchored once are gone.
        if (anchorList.Count > 0 && anchorList.Max(a => a.ChainIndex) > checkedCount)
        {
            return Broken(checkedCount + 1,
                $"The audit chain ends at position {checkedCount:N0}, but position {anchorList.Max(a => a.ChainIndex):N0} was anchored outside the database. Records have been removed, or the database was restored from a backup older than that anchor.");
        }

        var unsealed = await db.Set<AuditEvent>().LongCountAsync(e => e.Hash == null, cancellationToken);
        var anchorsNote = anchorList.Count == 0
            ? " No anchors outside the database yet."
            : $" It matches the {anchorList.Count:N0} anchor(s) outside the database." + (unreadableAnchors > 0 ? $" {unreadableAnchors} anchor line(s) could not be read." : "");
        return new AuditVerification(true, checkedCount, null, unsealed, $"Verified {checkedCount:N0} audit records.{anchorsNote}")
        {
            AnchorsChecked = anchorList.Count,
            LastAnchor = lastAnchor,
        };
    }
}

/// <summary>Seals new audit events every few seconds, and anchors the chain's end outside the database every so often.</summary>
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

                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var interval = options.Value.Audit.AnchorInterval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : options.Value.Audit.AnchorInterval;
                    await scope.ServiceProvider.GetRequiredService<AuditChainService>().AnchorIfDueAsync(interval, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit sealing failed");
            }
        }
    }
}
