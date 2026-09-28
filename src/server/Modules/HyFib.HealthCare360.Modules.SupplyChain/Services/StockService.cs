using System.Buffers.Binary;
using System.Security.Cryptography;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using Npgsql;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>The document behind a movement; stamped on every ledger row and reservation ("pharmacy.dispense", id, number).</summary>
public sealed record StockReference(string Module, Guid? Id, string? Number = null, string? Remarks = null);

/// <summary>
/// One item wanted by the caller. <paramref name="BatchId"/> restricts the issue to a scanned batch;
/// <paramref name="LineId"/> is echoed back on the resulting movements so callers can map batches to their lines;
/// <paramref name="MinimumShelfLifeDays"/> overrides the request's shelf-life rule for this line.
/// </summary>
public sealed record StockDemand(Guid ItemId, decimal Quantity, Guid? BatchId = null, Guid? LineId = null, int? MinimumShelfLifeDays = null);

/// <param name="Movement">Issue, Sale, Dispense, Consumption or TransferOut.</param>
/// <param name="ConsumeReservations">Use stock reserved under (<c>Reference.Module</c>, <c>Reference.Id</c>) first.</param>
public sealed record StockIssueRequest(
    Guid StoreId,
    StockMovementType Movement,
    StockReference Reference,
    IReadOnlyList<StockDemand> Lines,
    bool AllowPartial = false,
    int MinimumShelfLifeDays = 0,
    bool ConsumeReservations = false,
    Guid? CounterpartStoreId = null);

/// <summary>A batch that moved, with what callers need for their own documents (invoice line, issue slip, register).</summary>
public sealed record StockMovement(
    Guid? LineId,
    Guid ItemId,
    string ItemName,
    Guid StoreId,
    Guid BatchId,
    string BatchNumber,
    DateOnly? ExpiryDate,
    DateOnly? ManufactureDate,
    decimal Quantity,
    decimal Mrp,
    decimal UnitCost,
    decimal GstRate,
    decimal BalanceAfter,
    Guid LedgerEntryId,
    bool RequiresRegister);

public sealed record StockShortfall(Guid? LineId, Guid ItemId, string ItemName, decimal Requested, decimal Issued);

public sealed record StockIssueResult(IReadOnlyList<StockMovement> Movements, IReadOnlyList<StockShortfall> Shortfalls)
{
    public decimal IssuedFor(Guid? lineId) => Movements.Where(m => m.LineId == lineId).Sum(m => m.Quantity);
}

/// <summary>Stock coming into a store (GRN, transfer-in, opening balance). Rates are per base unit.</summary>
public sealed record StockReceipt(
    Guid StoreId,
    Guid ItemId,
    string BatchNumber,
    DateOnly? ExpiryDate,
    DateOnly? ManufactureDate,
    decimal Quantity,
    decimal Mrp,
    decimal UnitCost,
    decimal GstRate,
    Guid? VendorId = null,
    Guid? GoodsReceiptId = null,
    DateOnly? ReceivedOn = null);

/// <summary>
/// Stock used on a patient or in a procedure outside the pharmacy counter: OT implants and consumables, CSSD packs,
/// procedure-room items. <paramref name="BatchNumber"/> is the lot printed on the pack (as written in the OT notes);
/// without it FEFO chooses the batch. <paramref name="ExpiryDate"/> tells apart two lots with the same number.
/// <paramref name="Reference"/> names the consuming record (module, record id, document number) and must carry its
/// id, so the movement can be reversed with <see cref="StockService.ReverseConsumptionAsync"/>.
/// </summary>
public sealed record StockConsumption(
    Guid StoreId,
    Guid ItemId,
    decimal Quantity,
    StockReference Reference,
    string? BatchNumber = null,
    DateOnly? ExpiryDate = null);

/// <summary>A consumption or return ledger row of one consuming record, for working out what is still to reverse.</summary>
internal sealed record ConsumptionEntry(Guid LedgerEntryId, Guid BatchId, decimal Quantity);

/// <summary>Read model of a batch for pickers and availability checks.</summary>
public sealed record BatchStock(
    Guid BatchId,
    Guid StoreId,
    Guid ItemId,
    string BatchNumber,
    DateOnly? ExpiryDate,
    decimal Mrp,
    decimal UnitCost,
    decimal GstRate,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    bool IsExpired,
    bool IsBlocked,
    string? BlockReason,
    int? DaysToExpiry);

/// <summary>
/// The only way stock quantities change (INV-04); used by SupplyChain documents and by other modules (Pharmacy
/// dispensing, OT implants, CSSD consumption):
/// <list type="bullet">
/// <item>FEFO allocation through <see cref="FefoAllocator"/> — never from expired or blocked batches.</item>
/// <item>Movements are staged in the caller's unit of work; the caller saves (<see cref="SaveAsync"/>) and commits,
/// so its document and the stock change commit together. Call inside <c>db.Database.BeginTransactionAsync</c>.</item>
/// <item>Movements of one item in one store are serialised with a transaction-scoped PostgreSQL advisory lock, so
/// the running balances on the ledger (and the statutory registers built from them) are exact. Batch rows also
/// carry optimistic concurrency (<c>xmin</c>), reported as <see cref="StockErrors.ConcurrentUpdate"/>.</item>
/// <item>Business failures are <see cref="Result"/> errors ("stock.insufficient", "stock.batch_expired"…).</item>
/// </list>
/// </summary>
public sealed class StockService(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    /// <summary>Batch number used for items that are not batch-tracked (linen, spares).</summary>
    public const string NoBatch = "NA";

    private DateTimeOffset _clock;

    public DateOnly Today => HospitalClock.Today(time);

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<BatchStock>> GetBatchesAsync(Guid storeId, Guid itemId, bool includeEmpty, CancellationToken ct)
    {
        var today = Today;
        var batches = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.StoreId == storeId && b.ItemId == itemId && (includeEmpty || b.QuantityOnHand > 0))
            .OrderBy(b => b.ExpiryDate == null)
            .ThenBy(b => b.ExpiryDate)
            .ThenBy(b => b.ReceivedOn)
            .ToListAsync(ct);
        return batches.Select(b => ToBatchStock(b, today)).ToList();
    }

    /// <summary>Quantity FEFO could hand out today per item (unreserved, unexpired, unblocked).</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetAvailableAsync(Guid storeId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        var today = Today;
        var ids = itemIds.Distinct().ToList();
        var rows = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.StoreId == storeId && ids.Contains(b.ItemId) && b.QuantityOnHand > b.QuantityReserved && !b.IsBlocked
                        && (b.ExpiryDate == null || b.ExpiryDate >= today))
            .GroupBy(b => b.ItemId)
            .Select(g => new { ItemId = g.Key, Available = g.Sum(b => b.QuantityOnHand - b.QuantityReserved) })
            .ToListAsync(ct);

        var result = ids.ToDictionary(id => id, _ => 0m);
        foreach (var row in rows)
        {
            result[row.ItemId] = row.Available;
        }

        return result;
    }

    /// <summary>Preview of what FEFO would issue now (no changes, no locks).</summary>
    public async Task<Result<AllocationPlan>> PlanAsync(Guid storeId, Guid itemId, decimal quantity, AllocationOptions? options, CancellationToken ct)
    {
        var batches = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.StoreId == storeId && b.ItemId == itemId && b.QuantityOnHand > 0)
            .ToListAsync(ct);
        return FefoAllocator.Allocate(quantity, batches.Select(BatchCandidate.From), Today, options);
    }

    // ------------------------------------------------------------------ movements

    /// <summary>Takes stock out of a store: reserved stock first (when asked), then an explicit batch or FEFO.</summary>
    public async Task<Result<StockIssueResult>> IssueAsync(StockIssueRequest request, CancellationToken ct)
    {
        if (request.Movement is not (StockMovementType.Issue or StockMovementType.Sale or StockMovementType.Dispense
            or StockMovementType.Consumption or StockMovementType.TransferOut))
        {
            throw new ArgumentException($"{request.Movement} is not an outward movement.", nameof(request));
        }

        if (request.Lines.Count == 0)
        {
            return Error.Rule("stock.no_lines", "Nothing to issue.");
        }

        if (request.Lines.Any(l => l.Quantity <= 0))
        {
            return StockErrors.InvalidQuantity;
        }

        var store = await FindStoreAsync(request.StoreId, requireActive: true, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        await LockAsync(request.Lines.Select(l => (request.StoreId, l.ItemId)), ct);

        var today = Today;
        var movements = new List<StockMovement>();
        var shortfalls = new List<StockShortfall>();
        foreach (var line in request.Lines)
        {
            var slot = await LoadSlotAsync(store.Value, line.ItemId, ct);
            if (slot.IsFailure)
            {
                return slot.Error!;
            }

            var remaining = line.Quantity;
            if (request.ConsumeReservations && request.Reference.Id is { } referenceId)
            {
                var consumed = await ConsumeReservationsAsync(slot.Value, request, line, referenceId, today, movements, ct);
                if (consumed.IsFailure)
                {
                    return consumed.Error!;
                }

                remaining -= consumed.Value;
            }

            if (remaining > 0)
            {
                var shelfLife = line.MinimumShelfLifeDays ?? request.MinimumShelfLifeDays;
                var plan = await PlanForSlotAsync(slot.Value, line, remaining, request.AllowPartial, shelfLife, today, ct);
                if (plan.IsFailure)
                {
                    return plan.Error!;
                }

                foreach (var allocation in plan.Value.Allocations)
                {
                    var batch = slot.Value.Batch(allocation.BatchId);
                    var deducted = batch.Deduct(allocation.Quantity);
                    if (deducted.IsFailure)
                    {
                        return deducted.Error!;
                    }

                    movements.Add(Post(slot.Value, batch, request.Movement, -allocation.Quantity, request.Reference, request.CounterpartStoreId, line.LineId));
                }

                remaining -= plan.Value.Allocated;
            }

            if (remaining > 0)
            {
                shortfalls.Add(new StockShortfall(line.LineId, line.ItemId, slot.Value.Item.Name, line.Quantity, line.Quantity - remaining));
            }
        }

        return new StockIssueResult(movements, shortfalls);
    }

    /// <summary>Books stock in: tops up the matching batch (same number, expiry and MRP) or creates it.</summary>
    public async Task<Result<StockMovement>> ReceiveAsync(StockReceipt receipt, StockMovementType movement, StockReference reference, Guid? counterpartStoreId, CancellationToken ct)
    {
        if (movement is not (StockMovementType.Grn or StockMovementType.TransferIn or StockMovementType.Opening or StockMovementType.Return))
        {
            throw new ArgumentException($"{movement} is not an inward movement.", nameof(movement));
        }

        if (receipt.Quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        var store = await FindStoreAsync(receipt.StoreId, requireActive: true, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        await LockAsync([(receipt.StoreId, receipt.ItemId)], ct);
        var slot = await LoadSlotAsync(store.Value, receipt.ItemId, ct);
        if (slot.IsFailure)
        {
            return slot.Error!;
        }

        var batchNumber = NormalizeBatch(receipt.BatchNumber);
        var mrp = Math.Round(receipt.Mrp, 4, MidpointRounding.AwayFromZero);
        var expiry = receipt.ExpiryDate;
        var batch = slot.Value.Batches.FirstOrDefault(b => b.BatchNumber == batchNumber && b.ExpiryDate == expiry && b.Mrp == mrp)
            ?? await db.Set<StockBatch>().FirstOrDefaultAsync(
                b => b.StoreId == receipt.StoreId && b.ItemId == receipt.ItemId && b.BatchNumber == batchNumber && b.ExpiryDate == expiry && b.Mrp == mrp, ct);
        if (batch is null)
        {
            batch = new StockBatch
            {
                FacilityId = store.Value.FacilityId,
                StoreId = receipt.StoreId,
                ItemId = receipt.ItemId,
                BatchNumber = batchNumber,
                ExpiryDate = expiry,
                ManufactureDate = receipt.ManufactureDate,
                Mrp = mrp,
                GstRate = receipt.GstRate,
                VendorId = receipt.VendorId,
                GoodsReceiptId = receipt.GoodsReceiptId,
                ReceivedOn = receipt.ReceivedOn ?? Today,
            };
            db.Add(batch);
        }

        slot.Value.Track(batch);
        var received = batch.Receive(receipt.Quantity, Math.Round(receipt.UnitCost, 4, MidpointRounding.AwayFromZero));
        if (received.IsFailure)
        {
            return received.Error!;
        }

        return Post(slot.Value, batch, movement, receipt.Quantity, reference, counterpartStoreId, null);
    }

    /// <summary>Puts stock back into the batch it left from (sales/ward return, cancelled transfer).</summary>
    public async Task<Result<StockMovement>> ReturnToBatchAsync(Guid batchId, decimal quantity, StockMovementType movement, StockReference reference, Guid? counterpartStoreId, CancellationToken ct)
    {
        if (movement is not (StockMovementType.Return or StockMovementType.TransferIn))
        {
            throw new ArgumentException($"{movement} cannot return stock to a batch.", nameof(movement));
        }

        if (quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        var located = await LocateBatchAsync(batchId, ct);
        if (located.IsFailure)
        {
            return located.Error!;
        }

        var (slot, batch) = located.Value;
        var received = batch.Receive(quantity, batch.PurchaseRate);
        return received.IsFailure ? received.Error! : Post(slot, batch, movement, quantity, reference, counterpartStoreId, null);
    }

    /// <summary>Applies an approved correction to one batch (count variance, breakage, expiry write-off).</summary>
    public async Task<Result<StockMovement>> AdjustBatchAsync(Guid batchId, decimal delta, StockMovementType movement, StockReference reference, CancellationToken ct)
    {
        if (movement is not (StockMovementType.Adjustment or StockMovementType.ExpiryWriteOff))
        {
            throw new ArgumentException($"{movement} is not an adjustment.", nameof(movement));
        }

        var located = await LocateBatchAsync(batchId, ct);
        if (located.IsFailure)
        {
            return located.Error!;
        }

        var (slot, batch) = located.Value;
        var adjusted = batch.Adjust(delta);
        return adjusted.IsFailure ? adjusted.Error! : Post(slot, batch, movement, delta, reference, null, null);
    }

    /// <summary>
    /// Moves stock between two stores in one step (indent issue to a sub-store): transfer-out from the source and
    /// transfer-in of the same batch, expiry and MRP at the destination. Both sides are locked up front in one
    /// ordered pass so opposite transfers cannot deadlock.
    /// </summary>
    public async Task<Result<IReadOnlyList<StockMovement>>> TransferAsync(
        Guid fromStoreId, Guid toStoreId, IReadOnlyList<StockDemand> lines, StockReference reference, bool consumeReservations, CancellationToken ct)
    {
        if (fromStoreId == toStoreId)
        {
            return Error.Rule("stock.same_store", "Choose a different destination store.");
        }

        var destination = await FindStoreAsync(toStoreId, requireActive: true, ct);
        if (destination.IsFailure)
        {
            return destination.Error!;
        }

        await LockAsync(lines.SelectMany(l => new[] { (fromStoreId, l.ItemId), (toStoreId, l.ItemId) }), ct);

        var issued = await IssueAsync(
            new StockIssueRequest(fromStoreId, StockMovementType.TransferOut, reference, lines, ConsumeReservations: consumeReservations, CounterpartStoreId: toStoreId), ct);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        var movements = new List<StockMovement>(issued.Value.Movements);
        foreach (var moved in issued.Value.Movements)
        {
            var source = db.Set<StockBatch>().Local.First(b => b.Id == moved.BatchId);
            var received = await ReceiveAsync(
                new StockReceipt(toStoreId, moved.ItemId, source.BatchNumber, source.ExpiryDate, source.ManufactureDate, moved.Quantity, source.Mrp,
                    source.PurchaseRate, source.GstRate, source.VendorId, source.GoodsReceiptId, source.ReceivedOn),
                StockMovementType.TransferIn, reference, fromStoreId, ct);
            if (received.IsFailure)
            {
                return received.Error!;
            }

            movements.Add(received.Value with { LineId = moved.LineId });
        }

        return movements;
    }

    // ------------------------------------------------------------------ consumption by other modules

    /// <summary>
    /// The facility's active store of a type, for modules that consume stock without picking a store on screen
    /// (the OT store for implants, the CSSD store). Fails when there is none, or more than one to choose from.
    /// </summary>
    public async Task<Result<Guid>> ResolveStoreAsync(Guid facilityId, StoreType type, CancellationToken ct)
    {
        var stores = await db.Set<Store>().AsNoTracking()
            .Where(s => s.FacilityId == facilityId && s.Type == type && s.IsActive)
            .OrderBy(s => s.Code)
            .Select(s => s.Id)
            .Take(2)
            .ToListAsync(ct);
        var name = type == StoreType.Cssd ? "CSSD" : type.ToString();
        if (stores.Count == 0)
        {
            return Error.Rule("stock.no_store", $"There is no active {name} store in this facility. Set one up under Inventory > Stores.");
        }

        return stores.Count == 1
            ? stores[0]
            : Error.Rule("stock.store_ambiguous", $"This facility has more than one active {name} store; choose the store explicitly.");
    }

    /// <summary>
    /// Books stock used on a patient or in a procedure (OT implants and consumables, CSSD, procedure rooms) as a
    /// <see cref="StockMovementType.Consumption"/> in the caller's unit of work. With a lot number only that lot is
    /// used; otherwise FEFO. Never partial: the whole quantity moves or the call fails ("stock.insufficient",
    /// "stock.batch_not_found", "stock.batch_expired", "stock.batch_blocked"). Register items (Schedule H1/X, NDPS)
    /// also reach the controlled-drug register through <see cref="ControlledStockMoved"/>.
    /// </summary>
    public async Task<Result<StockIssueResult>> ConsumeAsync(StockConsumption consumption, CancellationToken ct)
    {
        if (consumption.Reference.Id is null)
        {
            throw new ArgumentException("Consumption needs the id of the consuming record so that it can be reversed.", nameof(consumption));
        }

        if (string.IsNullOrWhiteSpace(consumption.BatchNumber))
        {
            return await IssueAsync(ConsumptionRequest(consumption, [new StockDemand(consumption.ItemId, consumption.Quantity)]), ct);
        }

        if (consumption.Quantity <= 0)
        {
            return StockErrors.InvalidQuantity;
        }

        var store = await FindStoreAsync(consumption.StoreId, requireActive: true, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        await LockAsync([(consumption.StoreId, consumption.ItemId)], ct);
        var slot = await LoadSlotAsync(store.Value, consumption.ItemId, ct);
        if (slot.IsFailure)
        {
            return slot.Error!;
        }

        var plan = FefoAllocator.AllocateLot(
            consumption.Quantity, NormalizeBatch(consumption.BatchNumber), consumption.ExpiryDate, slot.Value.Batches.Select(BatchCandidate.From), Today,
            slot.Value.Item.Name);
        if (plan.IsFailure)
        {
            return plan.Error!;
        }

        var lines = plan.Value.Allocations.Select(a => new StockDemand(consumption.ItemId, a.Quantity, a.BatchId)).ToList();
        return await IssueAsync(ConsumptionRequest(consumption, lines), ct);
    }

    /// <summary>
    /// Puts back everything still consumed under <paramref name="reference"/> (module + record id) into the batches
    /// it came from, for example when an OT entry is removed as wrongly recorded. Idempotent: once reversed, a
    /// further call finds nothing outstanding and returns no movements.
    /// </summary>
    public async Task<Result<IReadOnlyList<StockMovement>>> ReverseConsumptionAsync(StockReference reference, CancellationToken ct)
    {
        if (reference.Id is not { } referenceId)
        {
            throw new ArgumentException("A reversal needs the id of the consuming record.", nameof(reference));
        }

        var module = reference.Module;
        var saved = await db.Set<StockLedgerEntry>().AsNoTracking()
            .Where(e => e.ReferenceModule == module && e.ReferenceId == referenceId
                        && (e.MovementType == StockMovementType.Consumption || e.MovementType == StockMovementType.Return))
            .Select(e => new ConsumptionEntry(e.Id, e.BatchId, e.Quantity))
            .ToListAsync(ct);

        // Movements staged earlier in this unit of work are not in the database yet.
        var staged = db.Set<StockLedgerEntry>().Local
            .Where(e => e.ReferenceModule == module && e.ReferenceId == referenceId
                        && (e.MovementType is StockMovementType.Consumption or StockMovementType.Return))
            .Select(e => new ConsumptionEntry(e.Id, e.BatchId, e.Quantity));

        var movements = new List<StockMovement>();
        foreach (var (batchId, quantity) in Outstanding(saved.Concat(staged)))
        {
            var returned = await ReturnToBatchAsync(batchId, quantity, StockMovementType.Return, reference, null, ct);
            if (returned.IsFailure)
            {
                return returned.Error!;
            }

            movements.Add(returned.Value);
        }

        return movements;
    }

    /// <summary>Net quantity still consumed per batch: consumptions are negative ledger quantities, returns positive.</summary>
    internal static IReadOnlyList<(Guid BatchId, decimal Quantity)> Outstanding(IEnumerable<ConsumptionEntry> entries) =>
        entries.DistinctBy(e => e.LedgerEntryId)
            .GroupBy(e => e.BatchId)
            .Select(g => (BatchId: g.Key, Quantity: -g.Sum(e => e.Quantity)))
            .Where(x => x.Quantity > 0)
            .OrderBy(x => x.BatchId)
            .ToList();

    private static StockIssueRequest ConsumptionRequest(StockConsumption consumption, IReadOnlyList<StockDemand> lines) =>
        new(consumption.StoreId, StockMovementType.Consumption, consumption.Reference, lines);

    // ------------------------------------------------------------------ reservations

    /// <summary>Sets stock aside for a document (FEFO across batches). With <paramref name="allowPartial"/> reserves what is available.</summary>
    public async Task<Result<IReadOnlyList<StockReservation>>> ReserveAsync(
        Guid storeId, Guid itemId, decimal quantity, StockReference reference, Guid? lineId, bool allowPartial, CancellationToken ct)
    {
        if (reference.Id is not { } referenceId)
        {
            throw new ArgumentException("Reservations need a reference id.", nameof(reference));
        }

        var store = await FindStoreAsync(storeId, requireActive: true, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        await LockAsync([(storeId, itemId)], ct);
        var slot = await LoadSlotAsync(store.Value, itemId, ct);
        if (slot.IsFailure)
        {
            return slot.Error!;
        }

        var today = Today;
        var plan = FefoAllocator.Allocate(quantity, slot.Value.Batches.Select(BatchCandidate.From), today, new AllocationOptions(allowPartial));
        if (plan.IsFailure)
        {
            return StockErrors.Insufficient(slot.Value.Item.Name, FefoAllocator.Available(slot.Value.Batches.Select(BatchCandidate.From), today), quantity);
        }

        var now = time.GetUtcNow();
        var reservations = new List<StockReservation>();
        foreach (var allocation in plan.Value.Allocations)
        {
            var reserved = slot.Value.Batch(allocation.BatchId).Reserve(allocation.Quantity);
            if (reserved.IsFailure)
            {
                return reserved.Error!;
            }

            var reservation = new StockReservation
            {
                FacilityId = store.Value.FacilityId,
                StoreId = storeId,
                ItemId = itemId,
                BatchId = allocation.BatchId,
                Quantity = allocation.Quantity,
                ReferenceModule = reference.Module,
                ReferenceId = referenceId,
                ReferenceLineId = lineId,
                ReferenceNumber = reference.Number,
                CreatedAt = now,
                CreatedBy = currentUser.UserId,
            };
            db.Add(reservation);
            reservations.Add(reservation);
        }

        return reservations;
    }

    /// <summary>Releases everything still reserved for a document (or one of its lines); returns the quantity released.</summary>
    public async Task<Result<decimal>> ReleaseReservationsAsync(string module, Guid referenceId, Guid? lineId, CancellationToken ct)
    {
        var reservations = await db.Set<StockReservation>()
            .Where(r => r.ReferenceModule == module && r.ReferenceId == referenceId && r.Status == ReservationStatus.Active
                        && (lineId == null || r.ReferenceLineId == lineId))
            .ToListAsync(ct);
        if (reservations.Count == 0)
        {
            return 0m;
        }

        await LockAsync(reservations.Select(r => (r.StoreId, r.ItemId)), ct);
        var now = time.GetUtcNow();
        var total = 0m;
        foreach (var reservation in reservations)
        {
            var batch = await db.Set<StockBatch>().FirstAsync(b => b.Id == reservation.BatchId, ct);
            var released = reservation.Release(now);
            if (released <= 0)
            {
                continue;
            }

            var result = batch.ReleaseReservation(released);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            total += released;
        }

        return total;
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Saves the unit of work, turning concurrency and stock check-constraint failures into stock errors.</summary>
    public async Task<Result> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return StockErrors.ConcurrentUpdate;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            return Error.Conflict("stock.negative", "The stock changed and this movement would make it negative. Please retry.");
        }
    }

    /// <summary>
    /// Serialises movements of the same item in the same store until the caller's transaction ends
    /// (<c>pg_advisory_xact_lock</c>). Keys are taken in ascending order so two transactions never deadlock on them.
    /// </summary>
    public async Task LockAsync(IEnumerable<(Guid StoreId, Guid ItemId)> keys, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Stock movements must run inside db.Database.BeginTransactionAsync(): item/store locks and ledger balances depend on it.");
        }

        foreach (var key in keys.Select(k => LockKey(k.StoreId, k.ItemId)).Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        }
    }

    /// <summary>Stable 64-bit lock key for an item in a store (collisions only cause harmless extra waiting).</summary>
    internal static long LockKey(Guid storeId, Guid itemId)
    {
        Span<byte> buffer = stackalloc byte[32];
        storeId.TryWriteBytes(buffer[..16]);
        itemId.TryWriteBytes(buffer[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }

    internal static string NormalizeBatch(string? batchNumber) =>
        string.IsNullOrWhiteSpace(batchNumber) ? NoBatch : batchNumber.Trim().ToUpperInvariant();

    internal static BatchStock ToBatchStock(StockBatch b, DateOnly today) => new(
        b.Id, b.StoreId, b.ItemId, b.BatchNumber, b.ExpiryDate, b.Mrp, b.PurchaseRate, b.GstRate, b.QuantityOnHand, b.QuantityReserved,
        b.QuantityAvailable, b.IsExpired(today), b.IsBlocked, b.BlockReason, b.ExpiryDate is { } expiry ? expiry.DayNumber - today.DayNumber : null);

    // ------------------------------------------------------------------ internals

    private async Task<Result<decimal>> ConsumeReservationsAsync(
        StockSlot slot, StockIssueRequest request, StockDemand line, Guid referenceId, DateOnly today, List<StockMovement> movements, CancellationToken ct)
    {
        var module = request.Reference.Module;
        var lineId = line.LineId;
        var storeId = slot.Store.Id;
        var itemId = slot.Item.Id;
        var reservations = await db.Set<StockReservation>()
            .Where(r => r.ReferenceModule == module && r.ReferenceId == referenceId && r.StoreId == storeId && r.ItemId == itemId
                        && r.Status == ReservationStatus.Active && (lineId == null || r.ReferenceLineId == lineId))
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var consumed = 0m;
        foreach (var reservation in reservations)
        {
            if (consumed >= line.Quantity)
            {
                break;
            }

            var batch = await TrackBatchAsync(slot, reservation.BatchId, ct);
            if (batch is null || batch.EnsureIssuable(today).IsFailure || (line.BatchId is { } picked && picked != batch.Id))
            {
                // Expired/blocked reserved stock stays reserved until the document is closed and releases it.
                continue;
            }

            var take = Math.Min(reservation.Outstanding, line.Quantity - consumed);
            var fromBatch = batch.ConsumeReservation(take);
            if (fromBatch.IsFailure)
            {
                return fromBatch.Error!;
            }

            var fromReservation = reservation.Consume(take, now);
            if (fromReservation.IsFailure)
            {
                return fromReservation.Error!;
            }

            movements.Add(Post(slot, batch, request.Movement, -take, request.Reference, request.CounterpartStoreId, line.LineId));
            consumed += take;
        }

        return consumed;
    }

    private async Task<Result<AllocationPlan>> PlanForSlotAsync(
        StockSlot slot, StockDemand line, decimal quantity, bool allowPartial, int shelfLifeDays, DateOnly today, CancellationToken ct)
    {
        if (line.BatchId is { } batchId)
        {
            var batch = await TrackBatchAsync(slot, batchId, ct);
            if (batch is null)
            {
                return StockErrors.WrongBatch;
            }

            var usable = batch.EnsureIssuable(today);
            if (usable.IsFailure)
            {
                return usable.Error!;
            }

            if (shelfLifeDays > 0 && batch.ExpiryDate is { } expiry && expiry < today.AddDays(shelfLifeDays))
            {
                return StockErrors.ShortExpiry(batch.BatchNumber, batch.ExpiryDate);
            }

            var take = Math.Min(quantity, batch.QuantityAvailable);
            if (take < quantity && !allowPartial)
            {
                return StockErrors.Insufficient($"{slot.Item.Name} (batch {batch.BatchNumber})", batch.QuantityAvailable, quantity);
            }

            IReadOnlyList<BatchAllocation> allocations = take > 0 ? [new BatchAllocation(batch.Id, batch.BatchNumber, batch.ExpiryDate, take)] : [];
            return new AllocationPlan(allocations, quantity);
        }

        var candidates = slot.Batches.Select(BatchCandidate.From).ToList();
        var plan = FefoAllocator.Allocate(quantity, candidates, today, new AllocationOptions(allowPartial, shelfLifeDays));
        return plan.IsSuccess
            ? plan
            : StockErrors.Insufficient(slot.Item.Name, FefoAllocator.Available(candidates, today, shelfLifeDays), quantity);
    }

    private StockMovement Post(StockSlot slot, StockBatch batch, StockMovementType movement, decimal signedQuantity, StockReference reference, Guid? counterpartStoreId, Guid? lineId)
    {
        var entry = new StockLedgerEntry
        {
            FacilityId = batch.FacilityId,
            OccurredAt = Stamp(),
            StoreId = batch.StoreId,
            ItemId = batch.ItemId,
            BatchId = batch.Id,
            BatchNumber = batch.BatchNumber,
            ExpiryDate = batch.ExpiryDate,
            MovementType = movement,
            Quantity = signedQuantity,
            BatchBalanceAfter = batch.QuantityOnHand,
            BalanceAfter = slot.Balance,
            UnitCost = batch.PurchaseRate,
            Mrp = batch.Mrp,
            ReferenceModule = reference.Module,
            ReferenceId = reference.Id,
            ReferenceNumber = reference.Number,
            Remarks = reference.Remarks is { Length: > 300 } remarks ? remarks[..300] : reference.Remarks,
            UserId = currentUser.UserId,
            CounterpartStoreId = counterpartStoreId,
        };

        if (slot.Item.RequiresRegister)
        {
            entry.AnnounceRegisterMovement();
        }

        db.Add(entry);
        return new StockMovement(
            lineId, slot.Item.Id, slot.Item.Name, batch.StoreId, batch.Id, batch.BatchNumber, batch.ExpiryDate, batch.ManufactureDate,
            Math.Abs(signedQuantity), batch.Mrp, batch.PurchaseRate, batch.GstRate, entry.BalanceAfter, entry.Id, slot.Item.RequiresRegister);
    }

    /// <summary>Strictly increasing timestamps (1 µs apart) keep ledger rows of one transaction in posting order.</summary>
    private DateTimeOffset Stamp()
    {
        var now = time.GetUtcNow();
        _clock = now > _clock ? now : _clock.AddTicks(10);
        return _clock;
    }

    private async Task<Result<Store>> FindStoreAsync(Guid storeId, bool requireActive, CancellationToken ct)
    {
        var store = await db.Set<Store>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null)
        {
            return Error.NotFound(nameof(Store), storeId);
        }

        return requireActive && !store.IsActive ? Error.Rule("store.inactive", $"{store.Name} is inactive.") : store;
    }

    /// <summary>
    /// Loads the item's live batches in the store and merges batches already tracked by this unit of work (the
    /// database does not see unsaved changes, the change tracker does), so balances include earlier movements.
    /// </summary>
    private async Task<Result<StockSlot>> LoadSlotAsync(Store store, Guid itemId, CancellationToken ct)
    {
        var item = await db.Set<Item>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(Item), itemId);
        }

        var storeId = store.Id;
        await db.Set<StockBatch>()
            .Where(b => b.StoreId == storeId && b.ItemId == itemId && (b.QuantityOnHand > 0 || b.QuantityReserved > 0))
            .LoadAsync(ct);
        var batches = db.Set<StockBatch>().Local.Where(b => b.StoreId == storeId && b.ItemId == itemId).ToList();
        return new StockSlot(store, item, batches);
    }

    private async Task<Result<(StockSlot Slot, StockBatch Batch)>> LocateBatchAsync(Guid batchId, CancellationToken ct)
    {
        var located = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.Id == batchId)
            .Select(b => new { b.StoreId, b.ItemId })
            .FirstOrDefaultAsync(ct);
        if (located is null)
        {
            return Error.NotFound(nameof(StockBatch), batchId);
        }

        var store = await FindStoreAsync(located.StoreId, requireActive: false, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        await LockAsync([(located.StoreId, located.ItemId)], ct);
        var slot = await LoadSlotAsync(store.Value, located.ItemId, ct);
        if (slot.IsFailure)
        {
            return slot.Error!;
        }

        var batch = await TrackBatchAsync(slot.Value, batchId, ct);
        return batch is null ? Error.NotFound(nameof(StockBatch), batchId) : (slot.Value, batch);
    }

    private async Task<StockBatch?> TrackBatchAsync(StockSlot slot, Guid batchId, CancellationToken ct)
    {
        var storeId = slot.Store.Id;
        var itemId = slot.Item.Id;
        var batch = slot.Batches.FirstOrDefault(b => b.Id == batchId)
            ?? await db.Set<StockBatch>().FirstOrDefaultAsync(b => b.Id == batchId && b.StoreId == storeId && b.ItemId == itemId, ct);
        if (batch is not null)
        {
            slot.Track(batch);
        }

        return batch;
    }

    /// <summary>An item's batches in one store, as tracked by the current unit of work.</summary>
    private sealed class StockSlot(Store store, Item item, List<StockBatch> batches)
    {
        public Store Store { get; } = store;

        public Item Item { get; } = item;

        public List<StockBatch> Batches { get; } = batches;

        public decimal Balance => Batches.Sum(b => b.QuantityOnHand);

        public StockBatch Batch(Guid id) => Batches.First(b => b.Id == id);

        public void Track(StockBatch batch)
        {
            if (!Batches.Contains(batch))
            {
                Batches.Add(batch);
            }
        }
    }
}
