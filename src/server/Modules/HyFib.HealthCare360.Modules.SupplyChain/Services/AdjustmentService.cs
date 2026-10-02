using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Adjustments;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Stock adjustments with reason and second-person approval, expiry write-offs, and physical stock counts
/// (count sheet → variance → adjustment) for INV-07 stock audit.
/// </summary>
public sealed class AdjustmentService(HospitalDbContext db, INumberSeries numbers, StockService stock, ICurrentUser currentUser, TimeProvider time)
{
    public const string AdjustmentModule = "supplychain.adjustment";

    // ------------------------------------------------------------------ adjustments

    public async Task<Result<DocumentCreated>> CreateAsync(CreateAdjustmentRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Store>().AnyAsync(s => s.Id == request.StoreId, ct))
        {
            return Error.Field("storeId", "Choose a store of this facility.");
        }

        var batchIds = request.Lines.Select(l => l.BatchId).ToList();
        var batches = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => batchIds.Contains(b.Id) && b.StoreId == request.StoreId)
            .ToDictionaryAsync(b => b.Id, ct);
        if (batches.Count != batchIds.Count)
        {
            return Error.Field("lines", "Every batch must belong to the selected store.");
        }

        var itemNames = await ItemNamesAsync(batches.Values.Select(b => b.ItemId), ct);
        var lines = new List<StockAdjustmentLine>();
        foreach (var input in request.Lines)
        {
            var batch = batches[input.BatchId];
            if (input.QuantityChange < 0 && -input.QuantityChange > batch.QuantityAvailable)
            {
                return StockErrors.Insufficient($"{itemNames.GetValueOrDefault(batch.ItemId)} (batch {batch.BatchNumber})", batch.QuantityAvailable, -input.QuantityChange);
            }

            lines.Add(Line(batch, itemNames.GetValueOrDefault(batch.ItemId) ?? string.Empty, input.QuantityChange));
        }

        return await CreateCoreAsync(facilityId, request.StoreId, request.Reason, request.Remarks, lines, null, ct);
    }

    /// <summary>Raises (for approval) a write-off of every expired batch still holding unreserved stock in a store.</summary>
    public async Task<Result<DocumentCreated>> CreateExpiredWriteOffAsync(Guid storeId, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var today = HospitalClock.Today(time);
        var expired = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.StoreId == storeId && b.ExpiryDate < today && b.QuantityOnHand > b.QuantityReserved)
            .ToListAsync(ct);
        if (expired.Count == 0)
        {
            return Error.Rule("adjustment.no_expired_stock", "There is no expired stock to write off in this store.");
        }

        var itemNames = await ItemNamesAsync(expired.Select(b => b.ItemId), ct);
        var lines = expired.Select(b => Line(b, itemNames.GetValueOrDefault(b.ItemId) ?? string.Empty, -b.QuantityAvailable)).ToList();
        return await CreateCoreAsync(facilityId, storeId, AdjustmentReason.Expiry, "Expired stock write-off", lines, null, ct);
    }

    public async Task<Result> ApproveAsync(Guid id, CancellationToken ct)
    {
        var adjustment = await db.Set<StockAdjustment>().Include(a => a.Lines).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (adjustment is null)
        {
            return Error.NotFound(nameof(StockAdjustment), id);
        }

        var approved = adjustment.Approve(currentUser.UserId, time.GetUtcNow());
        if (approved.IsFailure)
        {
            return approved;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reference = new StockReference(AdjustmentModule, adjustment.Id, adjustment.Number, $"{adjustment.Reason}: {adjustment.Remarks}");
        foreach (var line in adjustment.Lines)
        {
            var moved = await stock.AdjustBatchAsync(line.BatchId, line.QuantityChange, adjustment.MovementType, reference, ct);
            if (moved.IsFailure)
            {
                return moved;
            }
        }

        if (adjustment.PhysicalCountId is { } countId)
        {
            var count = await db.Set<PhysicalStockCount>().FirstOrDefaultAsync(c => c.Id == countId, ct);
            count?.MarkAdjusted();
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RejectAsync(Guid id, string reason, CancellationToken ct)
    {
        var adjustment = await db.Set<StockAdjustment>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (adjustment is null)
        {
            return Error.NotFound(nameof(StockAdjustment), id);
        }

        var rejected = adjustment.Reject(currentUser.UserId, reason.Trim(), time.GetUtcNow());
        if (rejected.IsFailure)
        {
            return rejected;
        }

        if (adjustment.PhysicalCountId is { } countId)
        {
            var count = await db.Set<PhysicalStockCount>().FirstOrDefaultAsync(c => c.Id == countId, ct);
            count?.ReopenForRecount();
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ physical counts

    /// <summary>Generates the count sheet: every batch with stock in the store (optionally one item type or category).</summary>
    public async Task<Result<DocumentCreated>> StartCountAsync(StartCountRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Store>().AnyAsync(s => s.Id == request.StoreId && s.IsActive, ct))
        {
            return Error.Field("storeId", "Choose an active store of this facility.");
        }

        if (await db.Set<PhysicalStockCount>().AnyAsync(c => c.StoreId == request.StoreId && c.Status == StockCountStatus.InProgress, ct))
        {
            return Error.Conflict("count.in_progress", "A count is already in progress for this store. Submit or cancel it first.");
        }

        var rows = await (
                from b in db.Set<StockBatch>().AsNoTracking()
                join i in db.Set<Item>() on b.ItemId equals i.Id
                where b.StoreId == request.StoreId && b.QuantityOnHand > 0
                      && (request.ItemType == null || i.Type == request.ItemType)
                      && (request.CategoryId == null || i.CategoryId == request.CategoryId)
                orderby i.Name, b.ExpiryDate
                select new { Batch = b, i.Name })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return Error.Rule("count.nothing_to_count", "There is no stock in this store for the chosen filter.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var count = new PhysicalStockCount
        {
            FacilityId = facilityId,
            Number = await numbers.NextAsync(SupplyChainNumberSeries.StockCount, facilityId, ct),
            StoreId = request.StoreId,
            StartedAt = time.GetUtcNow(),
            StartedByUserId = currentUser.UserId,
            ItemTypeFilter = request.ItemType,
            CategoryFilterId = request.CategoryId,
            Remarks = request.Remarks?.Trim(),
            Lines = rows.Select(r => new StockCountLine
            {
                ItemId = r.Batch.ItemId,
                ItemName = r.Name,
                BatchId = r.Batch.Id,
                BatchNumber = r.Batch.BatchNumber,
                ExpiryDate = r.Batch.ExpiryDate,
                SystemQuantity = r.Batch.QuantityOnHand,
                UnitCost = r.Batch.PurchaseRate,
            }).ToList(),
        };
        db.Add(count);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentCreated(count.Id, count.Number);
    }

    public async Task<Result> RecordCountsAsync(Guid id, RecordCountsRequest request, CancellationToken ct)
    {
        var count = await db.Set<PhysicalStockCount>().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (count is null)
        {
            return Error.NotFound(nameof(PhysicalStockCount), id);
        }

        foreach (var entry in request.Lines)
        {
            var recorded = count.RecordCount(entry.LineId, entry.CountedQuantity, entry.Remarks);
            if (recorded.IsFailure)
            {
                return recorded;
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Freezes variances against current book stock and raises one adjustment for approval (if anything differs).</summary>
    public async Task<Result<Guid?>> SubmitCountAsync(Guid id, CancellationToken ct)
    {
        var count = await db.Set<PhysicalStockCount>().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (count is null)
        {
            return Error.NotFound(nameof(PhysicalStockCount), id);
        }

        var batchIds = count.Lines.Select(l => l.BatchId).ToList();
        var batches = await db.Set<StockBatch>().AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var submitted = count.Submit(batches.ToDictionary(b => b.Key, b => b.Value.QuantityOnHand), time.GetUtcNow());
        if (submitted.IsFailure)
        {
            return submitted.Error!;
        }

        var variances = count.Lines.Where(l => l.VarianceQuantity is { } variance && variance != 0 && batches.ContainsKey(l.BatchId)).ToList();
        if (variances.Count == 0)
        {
            count.MarkAdjusted();
            await db.SaveChangesAsync(ct);
            return Result<Guid?>.Ok(null);
        }

        var lines = variances.Select(l => Line(batches[l.BatchId], l.ItemName, l.VarianceQuantity!.Value)).ToList();
        var created = await CreateCoreAsync(count.FacilityId, count.StoreId, AdjustmentReason.CountVariance, $"Physical count {count.Number}", lines, count, ct);
        return created.IsFailure ? created.Error! : Result<Guid?>.Ok(created.Value.Id);
    }

    public async Task<Result> CancelCountAsync(Guid id, CancellationToken ct)
    {
        var count = await db.Set<PhysicalStockCount>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (count is null)
        {
            return Error.NotFound(nameof(PhysicalStockCount), id);
        }

        var cancelled = count.Cancel(time.GetUtcNow());
        if (cancelled.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return cancelled;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Result<DocumentCreated>> CreateCoreAsync(
        Guid facilityId, Guid storeId, AdjustmentReason reason, string? remarks, List<StockAdjustmentLine> lines, PhysicalStockCount? count, CancellationToken ct)
    {
        var adjustment = new StockAdjustment
        {
            FacilityId = facilityId,
            StoreId = storeId,
            Reason = reason,
            Remarks = remarks?.Trim(),
            RequestedByUserId = currentUser.UserId,
            RequestedAt = time.GetUtcNow(),
            PhysicalCountId = count?.Id,
            Lines = lines,
            NetValue = lines.Sum(l => l.Value),
        };
        var valid = adjustment.ValidateLines();
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        adjustment.Number = await numbers.NextAsync(SupplyChainNumberSeries.Adjustment, facilityId, ct);
        db.Add(adjustment);
        count?.LinkAdjustment(adjustment.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentCreated(adjustment.Id, adjustment.Number);
    }

    private static StockAdjustmentLine Line(StockBatch batch, string itemName, decimal change) => new()
    {
        ItemId = batch.ItemId,
        ItemName = itemName,
        BatchId = batch.Id,
        BatchNumber = batch.BatchNumber,
        ExpiryDate = batch.ExpiryDate,
        SystemQuantity = batch.QuantityOnHand,
        QuantityChange = change,
        UnitCost = batch.PurchaseRate,
        Value = Money.Round(change * batch.PurchaseRate),
    };

    private Task<Dictionary<Guid, string>> ItemNamesAsync(IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        return db.Set<Item>().AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
    }
}
