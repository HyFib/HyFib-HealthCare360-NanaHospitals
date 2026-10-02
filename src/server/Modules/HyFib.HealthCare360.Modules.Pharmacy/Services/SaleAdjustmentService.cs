using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// After-sale changes to pharmacy invoices (PH-02): collecting credit balances, same-day cancellation (full
/// restock, queue re-opened) and sales returns (restock unexpired stock, refund against dues first). Controlled
/// items going back on the shelf are written to the register.
/// </summary>
public sealed class SaleAdjustmentService(
    HospitalDbContext db,
    INumberSeries numbers,
    StockService stock,
    ControlledDrugRegisterService register,
    PharmacyRealtime realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result> CollectAsync(Guid saleId, CollectPaymentRequest request, CancellationToken ct)
    {
        var sale = await db.Set<PharmacySale>().Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), saleId);
        }

        var collected = sale.CollectPayment(request.Mode, request.Amount, request.Reference, time.GetUtcNow(), currentUser.UserId);
        if (collected.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return collected;
    }

    public async Task<Result> CancelAsync(Guid saleId, string reason, CancellationToken ct)
    {
        var sale = await LoadAsync(saleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), saleId);
        }

        var now = time.GetUtcNow();
        var cancelled = sale.Cancel(reason.Trim(), time.Today(), HospitalClock.ToLocalDate(sale.SoldAt), now, currentUser.UserId);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reference = new StockReference(PharmacyStockReferences.SaleCancel, sale.Id, sale.InvoiceNumber, $"Invoice cancelled: {sale.CancelReason}");
        var movements = new List<StockMovement>();
        foreach (var item in sale.Items)
        {
            var moved = await stock.ReturnToBatchAsync(item.BatchId, item.Quantity, StockMovementType.Return, reference, null, ct);
            if (moved.IsFailure)
            {
                return moved;
            }

            movements.Add(moved.Value);
        }

        PharmacyQueueEntry? entry = null;
        if (sale.QueueEntryId is { } entryId)
        {
            entry = await db.Set<PharmacyQueueEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, ct);
            foreach (var item in sale.Items.Where(i => i.QueueLineId is not null))
            {
                entry?.ReverseDispensed(item.QueueLineId!.Value, item.Quantity, now);
            }
        }

        await RecordRegisterAsync(sale, movements, reference, now, ct);
        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        if (entry is not null)
        {
            await realtime.QueueChangedAsync(entry.FacilityId, entry.Id, entry.Status, ct);
        }

        return Result.Success();
    }

    public async Task<Result<SaleReturnCreated>> ReturnAsync(Guid saleId, SaleReturnRequest request, CancellationToken ct)
    {
        var sale = await LoadAsync(saleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), saleId);
        }

        if (sale.Status == SaleStatus.Cancelled)
        {
            return Error.Rule("sale.cancelled", $"Invoice {sale.InvoiceNumber} is cancelled.");
        }

        var today = time.Today();
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var saleReturn = new PharmacySaleReturn
        {
            FacilityId = sale.FacilityId,
            ReturnNumber = await numbers.NextAsync(PharmacyNumberSeries.SaleReturn, sale.FacilityId, ct),
            SaleId = sale.Id,
            PatientId = sale.PatientId,
            ReturnedAt = now,
            Reason = request.Reason.Trim(),
            RefundMode = request.RefundMode,
            RefundReference = request.RefundReference?.Trim(),
        };
        var reference = new StockReference(PharmacyStockReferences.SaleReturn, saleReturn.Id, saleReturn.ReturnNumber, $"Return against {sale.InvoiceNumber}");
        var movements = new List<StockMovement>();
        var refund = 0m;
        foreach (var input in request.Lines)
        {
            var item = sale.Items.FirstOrDefault(i => i.Id == input.SaleItemId);
            if (item is null)
            {
                return Error.NotFound(nameof(PharmacySaleItem), input.SaleItemId);
            }

            var lineRefund = item.Return(input.Quantity);
            if (lineRefund.IsFailure)
            {
                return lineRefund.Error!;
            }

            // Only unexpired stock goes back on the shelf; expired or damaged returns are refunded but not restocked.
            var restock = input.Restock && (item.ExpiryDate is not { } expiry || expiry >= today);
            if (restock)
            {
                var moved = await stock.ReturnToBatchAsync(item.BatchId, input.Quantity, StockMovementType.Return, reference, null, ct);
                if (moved.IsFailure)
                {
                    return moved.Error!;
                }

                movements.Add(moved.Value);
            }

            saleReturn.Items.Add(new PharmacySaleReturnItem
            {
                SaleReturnId = saleReturn.Id,
                SaleItemId = item.Id,
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                BatchId = item.BatchId,
                BatchNumber = item.BatchNumber,
                Quantity = input.Quantity,
                RefundAmount = lineRefund.Value,
                Restocked = restock,
            });
            refund += lineRefund.Value;
        }

        var applied = sale.ApplyReturn(refund, request.RefundMode, request.RefundReference, now, currentUser.UserId);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        saleReturn.RefundAmount = refund;
        saleReturn.AdjustedAgainstDue = applied.Value.AdjustedAgainstDue;
        saleReturn.CashRefund = applied.Value.CashRefund;
        db.Add(saleReturn);
        await RecordRegisterAsync(sale, movements, reference, now, ct);

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new SaleReturnCreated(saleReturn.Id, saleReturn.ReturnNumber, refund, saleReturn.AdjustedAgainstDue, saleReturn.CashRefund);
    }

    private async Task RecordRegisterAsync(PharmacySale sale, List<StockMovement> movements, StockReference reference, DateTimeOffset now, CancellationToken ct)
    {
        if (!movements.Any(m => m.RequiresRegister))
        {
            return;
        }

        var items = await InventoryQueries.ItemSummariesAsync(db, movements.Select(m => m.ItemId), ct);
        var patient = sale.PatientId is { } patientId ? await PharmacyLookups.PatientAsync(db, patientId, ct) : null;
        register.Record(sale.FacilityId, movements, StockMovementType.Return, inward: true, reference,
            PharmacyLookups.Party(patient, sale.CustomerName, sale.PrescriberName, sale.PrescriberRegistrationNumber, sale.PrescriptionNumber), items, now);
    }

    private Task<PharmacySale?> LoadAsync(Guid saleId, CancellationToken ct) =>
        db.Set<PharmacySale>().Include(s => s.Items).Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == saleId, ct);
}
