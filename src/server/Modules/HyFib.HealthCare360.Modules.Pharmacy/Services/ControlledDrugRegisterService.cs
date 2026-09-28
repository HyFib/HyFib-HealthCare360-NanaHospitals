using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>Stock reference modules used by pharmacy documents (ledger rows and register lines).</summary>
public static class PharmacyStockReferences
{
    public const string Prefix = "pharmacy.";
    public const string Dispense = "pharmacy.dispense";
    public const string CounterSale = "pharmacy.sale";
    public const string SaleReturn = "pharmacy.return";
    public const string SaleCancel = "pharmacy.cancel";
    public const string IpIssue = "pharmacy.ip_issue";
    public const string WardReturn = "pharmacy.ward_return";
}

/// <summary>Who a register line is about: the patient, the prescriber and the prescription.</summary>
public sealed record RegisterParty(
    Guid? PatientId,
    string? PatientName,
    string? PatientUhid,
    string? PatientAddress,
    string? PrescriberName,
    string? PrescriberRegistrationNumber,
    string? PrescriptionNumber);

/// <summary>
/// Writes the statutory register (PH-06) for Schedule H1 / X / controlled items. Pharmacy documents record their
/// own movements with patient and prescriber; every other movement of a register item (GRN, transfer, write-off)
/// arrives through <see cref="ControlledStockMoved"/>, so the register shows every change with its balance.
/// </summary>
public sealed class ControlledDrugRegisterService(HospitalDbContext db, ICurrentUser currentUser)
{
    public void Record(
        Guid facilityId,
        IEnumerable<StockMovement> movements,
        StockMovementType movementType,
        bool inward,
        StockReference reference,
        RegisterParty party,
        IReadOnlyDictionary<Guid, ItemSummary> items,
        DateTimeOffset at)
    {
        foreach (var moved in movements.Where(m => m.RequiresRegister))
        {
            var item = items.GetValueOrDefault(moved.ItemId);
            db.Add(new ControlledDrugRegisterEntry
            {
                FacilityId = facilityId,
                OccurredAt = at,
                StoreId = moved.StoreId,
                ItemId = moved.ItemId,
                ItemName = moved.ItemName,
                Schedule = item?.Schedule ?? DrugSchedule.None,
                IsControlled = item?.IsControlled ?? false,
                BatchNumber = moved.BatchNumber,
                ExpiryDate = moved.ExpiryDate,
                MovementType = movementType,
                QuantityIn = inward ? moved.Quantity : 0,
                QuantityOut = inward ? 0 : moved.Quantity,
                BalanceAfter = moved.BalanceAfter,
                PatientId = party.PatientId,
                PatientName = party.PatientName,
                PatientUhid = party.PatientUhid,
                PatientAddress = party.PatientAddress,
                PrescriberName = party.PrescriberName,
                PrescriberRegistrationNumber = party.PrescriberRegistrationNumber,
                PrescriptionNumber = party.PrescriptionNumber,
                ReferenceModule = reference.Module,
                ReferenceId = reference.Id,
                ReferenceNumber = reference.Number,
                LedgerEntryId = moved.LedgerEntryId,
                RecordedByUserId = currentUser.UserId,
                Remarks = reference.Remarks,
            });
        }
    }
}

/// <summary>Records non-pharmacy movements of register items (receipts, transfers, write-offs) in the same transaction.</summary>
internal sealed class ControlledStockMovedHandler(HospitalDbContext db) : IDomainEventHandler<ControlledStockMoved>
{
    public async Task HandleAsync(ControlledStockMoved moved, CancellationToken cancellationToken)
    {
        if (moved.ReferenceModule.StartsWith(PharmacyStockReferences.Prefix, StringComparison.Ordinal)
            || db.Set<ControlledDrugRegisterEntry>().Local.Any(r => r.LedgerEntryId == moved.LedgerEntryId))
        {
            return;
        }

        var item = await db.Set<Item>().AsNoTracking()
            .Where(i => i.Id == moved.ItemId)
            .Select(i => new { i.Name, i.Schedule, i.IsControlled })
            .FirstOrDefaultAsync(cancellationToken);
        db.Add(new ControlledDrugRegisterEntry
        {
            FacilityId = moved.FacilityId,
            OccurredAt = moved.MovedAt,
            StoreId = moved.StoreId,
            ItemId = moved.ItemId,
            ItemName = item?.Name ?? string.Empty,
            Schedule = item?.Schedule ?? DrugSchedule.None,
            IsControlled = item?.IsControlled ?? false,
            BatchNumber = moved.BatchNumber,
            ExpiryDate = moved.ExpiryDate,
            MovementType = moved.MovementType,
            QuantityIn = moved.Quantity > 0 ? moved.Quantity : 0,
            QuantityOut = moved.Quantity < 0 ? -moved.Quantity : 0,
            BalanceAfter = moved.BalanceAfter,
            ReferenceModule = moved.ReferenceModule,
            ReferenceId = moved.ReferenceId,
            ReferenceNumber = moved.ReferenceNumber,
            LedgerEntryId = moved.LedgerEntryId,
            RecordedByUserId = moved.UserId,
            Remarks = moved.Remarks,
        });
    }
}
