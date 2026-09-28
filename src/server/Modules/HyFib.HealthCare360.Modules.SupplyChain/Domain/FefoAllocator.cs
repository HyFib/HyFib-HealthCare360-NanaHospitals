namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>A batch that could supply stock, as seen by the allocator (a snapshot of <see cref="StockBatch"/>).</summary>
public sealed record BatchCandidate(Guid BatchId, string BatchNumber, DateOnly? ExpiryDate, DateOnly ReceivedOn, decimal Available, bool IsBlocked = false)
{
    public static BatchCandidate From(StockBatch batch) =>
        new(batch.Id, batch.BatchNumber, batch.ExpiryDate, batch.ReceivedOn, batch.QuantityAvailable, batch.IsBlocked);
}

public sealed record BatchAllocation(Guid BatchId, string BatchNumber, DateOnly? ExpiryDate, decimal Quantity);

public sealed record AllocationPlan(IReadOnlyList<BatchAllocation> Allocations, decimal Requested)
{
    public decimal Allocated => Allocations.Sum(a => a.Quantity);

    public decimal Shortfall => Requested - Allocated;

    public bool IsComplete => Shortfall <= 0;
}

/// <param name="AllowPartial">Return what is available instead of failing (partial dispensing / issue).</param>
/// <param name="MinimumShelfLifeDays">
/// Skip batches that expire within this many days — for example the length of a prescription course, so a
/// patient is never handed medicine that expires before the course ends.
/// </param>
/// <param name="PreferredBatchId">A batch scanned or picked by the user; used first when it is usable.</param>
public sealed record AllocationOptions(bool AllowPartial = false, int MinimumShelfLifeDays = 0, Guid? PreferredBatchId = null);

/// <summary>
/// First-Expiry-First-Out batch selection (a pure function, unit-tested). Blocked, empty and expired batches are
/// never allocated; the earliest expiry goes first, non-expiring stock last, ties broken by receipt date (FIFO) and
/// batch number so the result is deterministic.
/// </summary>
public static class FefoAllocator
{
    public static Result<AllocationPlan> Allocate(decimal quantity, IEnumerable<BatchCandidate> candidates, DateOnly today, AllocationOptions? options = null)
    {
        options ??= new AllocationOptions();
        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        var usable = Usable(candidates, today, options.MinimumShelfLifeDays)
            .OrderByDescending(c => c.BatchId == options.PreferredBatchId)
            .ThenBy(c => c.ExpiryDate is null)
            .ThenBy(c => c.ExpiryDate)
            .ThenBy(c => c.ReceivedOn)
            .ThenBy(c => c.BatchNumber, StringComparer.Ordinal)
            .ToList();

        var allocations = new List<BatchAllocation>();
        var remaining = quantity;
        foreach (var candidate in usable)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(candidate.Available, remaining);
            allocations.Add(new BatchAllocation(candidate.BatchId, candidate.BatchNumber, candidate.ExpiryDate, take));
            remaining -= take;
        }

        if (remaining > 0 && !options.AllowPartial)
        {
            return StockErrors.Insufficient(null, quantity - remaining, quantity);
        }

        return new AllocationPlan(allocations, quantity);
    }

    /// <summary>
    /// Allocation from one lot (the batch number printed on the pack), which is how OT implants and consumables are
    /// recorded. Rows sharing the lot number (a re-priced MRP, or two expiry dates) are used FEFO; <paramref name="expiryDate"/>
    /// narrows to one of them. A missing, expired or blocked lot is reported as such rather than as a plain shortage,
    /// so the user knows to check the pack.
    /// </summary>
    public static Result<AllocationPlan> AllocateLot(
        decimal quantity, string batchNumber, DateOnly? expiryDate, IEnumerable<BatchCandidate> candidates, DateOnly today, string? itemName = null)
    {
        var lot = candidates
            .Where(c => string.Equals(c.BatchNumber, batchNumber, StringComparison.OrdinalIgnoreCase) && (expiryDate is null || c.ExpiryDate == expiryDate))
            .ToList();
        if (lot.Count == 0)
        {
            return quantity <= 0 ? StockErrors.InvalidQuantity : StockErrors.LotNotFound(batchNumber);
        }

        var plan = Allocate(quantity, lot, today);
        if (plan.IsSuccess || quantity <= 0)
        {
            return plan;
        }

        var usable = Available(lot, today);
        var stocked = lot.Where(c => c.Available > 0).ToList();
        if (usable == 0 && stocked.Count > 0)
        {
            if (stocked.All(c => c.ExpiryDate < today))
            {
                return StockErrors.Expired(batchNumber, stocked.Max(c => c.ExpiryDate));
            }

            if (stocked.All(c => c.IsBlocked || c.ExpiryDate < today))
            {
                return StockErrors.Blocked(batchNumber, null);
            }
        }

        return StockErrors.Insufficient(itemName is null ? $"batch {batchNumber}" : $"{itemName} (batch {batchNumber})", usable, quantity);
    }

    /// <summary>Total that FEFO could hand out today under the same rules.</summary>
    public static decimal Available(IEnumerable<BatchCandidate> candidates, DateOnly today, int minimumShelfLifeDays = 0) =>
        Usable(candidates, today, minimumShelfLifeDays).Sum(c => c.Available);

    private static IEnumerable<BatchCandidate> Usable(IEnumerable<BatchCandidate> candidates, DateOnly today, int minimumShelfLifeDays)
    {
        // A batch is usable through its expiry day; with a shelf-life requirement it must outlast the course.
        var cutoff = today.AddDays(Math.Max(minimumShelfLifeDays, 0));
        return candidates.Where(c => !c.IsBlocked && c.Available > 0 && (c.ExpiryDate is null || c.ExpiryDate >= cutoff));
    }
}
