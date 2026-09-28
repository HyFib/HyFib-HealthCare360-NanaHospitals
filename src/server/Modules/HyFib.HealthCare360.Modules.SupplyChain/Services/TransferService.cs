using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Transfers;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Two-step stock transfers between stores (INV-06, PH-04): dispatch (transfer-out, goods in transit) and receipt
/// (transfer-in with the same batch, expiry and MRP). Cancelling an in-transit transfer returns the stock to the
/// exact source batches.
/// </summary>
public sealed class TransferService(HospitalDbContext db, INumberSeries numbers, StockService stock, ICurrentUser currentUser, TimeProvider time)
{
    public const string ReferenceModule = "supplychain.transfer";
    public const string Series = "supplychain.transfer";
    public const string Format = "{FAC}STN{YY}{SEQ:6}";

    public async Task<Result<DocumentCreated>> DispatchAsync(DispatchTransferRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var storeIds = new[] { request.FromStoreId, request.ToStoreId };
        if (await db.Set<Store>().CountAsync(s => storeIds.Contains(s.Id) && s.IsActive, ct) != 2)
        {
            return Error.Field("toStoreId", "Choose two different active stores of this facility.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var number = await numbers.NextAsync(Series, facilityId, Format, NumberResetPolicy.Yearly, ct);
        var transfer = new StockTransfer
        {
            FacilityId = facilityId,
            Number = number,
            FromStoreId = request.FromStoreId,
            ToStoreId = request.ToStoreId,
            DispatchedAt = time.GetUtcNow(),
            DispatchedByUserId = currentUser.UserId,
            Remarks = request.Remarks?.Trim(),
        };

        var issued = await stock.IssueAsync(
            new StockIssueRequest(
                request.FromStoreId,
                StockMovementType.TransferOut,
                new StockReference(ReferenceModule, transfer.Id, number),
                request.Lines.Select(l => new StockDemand(l.ItemId, l.Quantity, l.BatchId)).ToList(),
                CounterpartStoreId: request.ToStoreId),
            ct);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        foreach (var moved in issued.Value.Movements)
        {
            var source = db.Set<StockBatch>().Local.First(b => b.Id == moved.BatchId);
            transfer.Lines.Add(new StockTransferLine
            {
                StockTransferId = transfer.Id,
                ItemId = moved.ItemId,
                ItemName = moved.ItemName,
                SourceBatchId = moved.BatchId,
                BatchNumber = moved.BatchNumber,
                ExpiryDate = moved.ExpiryDate,
                ManufactureDate = source.ManufactureDate,
                Mrp = moved.Mrp,
                PurchaseRate = moved.UnitCost,
                GstRate = moved.GstRate,
                Quantity = moved.Quantity,
            });
        }

        transfer.TotalValue = Money.Round(transfer.Lines.Sum(l => l.Quantity * l.PurchaseRate));
        db.Add(transfer);
        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new DocumentCreated(transfer.Id, transfer.Number);
    }

    public async Task<Result> ReceiveAsync(Guid id, CancellationToken ct)
    {
        var transfer = await db.Set<StockTransfer>().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null)
        {
            return Error.NotFound(nameof(StockTransfer), id);
        }

        var received = transfer.Receive(currentUser.UserId, time.GetUtcNow());
        if (received.IsFailure)
        {
            return received;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reference = new StockReference(ReferenceModule, transfer.Id, transfer.Number);
        foreach (var line in transfer.Lines)
        {
            var moved = await stock.ReceiveAsync(
                new StockReceipt(transfer.ToStoreId, line.ItemId, line.BatchNumber, line.ExpiryDate, line.ManufactureDate, line.Quantity, line.Mrp,
                    line.PurchaseRate, line.GstRate),
                StockMovementType.TransferIn, reference, transfer.FromStoreId, ct);
            if (moved.IsFailure)
            {
                return moved;
            }

            line.DestinationBatchId = moved.Value.BatchId;
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public async Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var transfer = await db.Set<StockTransfer>().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null)
        {
            return Error.NotFound(nameof(StockTransfer), id);
        }

        var cancelled = transfer.Cancel(reason.Trim(), time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reference = new StockReference(ReferenceModule, transfer.Id, transfer.Number, "Transfer cancelled: stock returned to source");
        foreach (var line in transfer.Lines)
        {
            var returned = await stock.ReturnToBatchAsync(line.SourceBatchId, line.Quantity, StockMovementType.TransferIn, reference, transfer.ToStoreId, ct);
            if (returned.IsFailure)
            {
                return returned;
            }
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }
}
