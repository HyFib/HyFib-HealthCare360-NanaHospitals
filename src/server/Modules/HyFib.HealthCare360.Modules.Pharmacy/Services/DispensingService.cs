using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>Patient details printed on invoices and registers.</summary>
internal sealed record PatientSnapshot(Guid Id, string Name, string Uhid, string? Phone, Address Address);

/// <summary>Look-ups shared by the pharmacy services.</summary>
internal static class PharmacyLookups
{
    /// <summary>Dispensing happens only from active pharmacy stores of the signed-in facility.</summary>
    public static async Task<Result<Store>> PharmacyStoreAsync(HospitalDbContext db, Guid storeId, CancellationToken ct)
    {
        var store = await db.Set<Store>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null || !store.IsActive)
        {
            return Error.Field("storeId", "Choose an active pharmacy store of this facility.");
        }

        return store.Type == StoreType.Pharmacy ? store : Error.Rule("pharmacy.not_pharmacy_store", $"{store.Name} is not a pharmacy store.");
    }

    public static Task<PatientSnapshot?> PatientAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new PatientSnapshot(p.Id, p.FullName, p.Uhid, p.Phone, p.Address))
            .FirstOrDefaultAsync(ct);

    public static RegisterParty Party(PatientSnapshot? patient, string? customerName, string? prescriber, string? registration, string? prescriptionNumber) =>
        new(patient?.Id, patient?.Name ?? customerName, patient?.Uhid, patient?.Address.ToString(), prescriber, registration, prescriptionNumber);

    public static PharmacySaleItem SaleItem(Guid saleId, ItemSummary item, StockMovement moved, decimal discountPercent)
    {
        var line = new PharmacySaleItem
        {
            PharmacySaleId = saleId,
            ItemId = item.Id,
            ItemName = item.Name,
            GenericName = item.GenericName,
            HsnCode = item.HsnCode,
            Schedule = item.Schedule,
            IsControlled = item.IsControlled,
            BatchId = moved.BatchId,
            BatchNumber = moved.BatchNumber,
            ExpiryDate = moved.ExpiryDate,
            Quantity = moved.Quantity,
            UnitMrp = moved.Mrp,

            // GST in force on the day of sale (the item master), not the rate on the purchase invoice.
            GstRate = item.GstRate,
        };
        line.Price(discountPercent);
        return line;
    }

    public static Error Prefixed(Error error, string subject) => error with { Message = $"{subject}: {error.Message}" };
}

/// <summary>
/// OP dispensing (PH-01) and counter sales (PH-02). Batches are allocated FEFO through the SupplyChain
/// <see cref="StockService"/> — never expired, and for prescriptions never expiring before the course (up to 30
/// days) ends — substitutions must be generic equivalents with the prescriber's consent, Schedule H/H1/X rules are
/// enforced, controlled medicines are confirmed and written to the register, and the pharmacy's own GST invoice
/// is raised with the payment split. Everything commits in one transaction.
/// </summary>
public sealed class DispensingService(
    HospitalDbContext db,
    INumberSeries numbers,
    StockService stock,
    ControlledDrugRegisterService register,
    PharmacyRealtime realtime,
    PatientAccessGuard guard,
    ISettingsService settings,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string InvoiceSeries = "pharmacy.invoice";
    public const string InvoiceFormat = "{FAC}PH{YY}{SEQ:7}";
    public const string MaxDiscountSetting = "pharmacy.discount.max_percent";
    public const decimal DefaultMaxDiscountPercent = 10m;
    private const int MaxShelfLifeDays = 30;

    private sealed record PlannedLine(Guid Key, PharmacyQueueLine Line, DispenseLineRequest Request, ItemSummary Item, bool IsSubstitution);

    public async Task<Result<PharmacySaleCreated>> DispenseAsync(Guid entryId, DispenseRequest request, CancellationToken ct)
    {
        var entry = await db.Set<PharmacyQueueEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound(nameof(PharmacyQueueEntry), entryId);
        }

        if (entry.IsInpatient)
        {
            return Error.Rule("pharmacy.inpatient_prescription", "In-patient prescriptions are issued to the ward from the IP pharmacy.");
        }

        if (!entry.IsOpen)
        {
            return Error.Rule("queue.closed", $"Prescription {entry.PrescriptionNumber} is {entry.Status}.");
        }

        var access = await guard.EnsureCanOpenAsync(entry.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var store = await PharmacyLookups.PharmacyStoreAsync(db, request.StoreId, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        var discount = await CheckDiscountAsync(store.Value.FacilityId, request.DiscountPercent, ct);
        if (discount.IsFailure)
        {
            return discount.Error!;
        }

        var itemIds = request.Lines.Select(l => l.ItemId).Concat(entry.Lines.Where(l => l.DrugItemId != null).Select(l => l.DrugItemId!.Value));
        var items = await InventoryQueries.ItemSummariesAsync(db, itemIds, ct);
        var planned = new List<PlannedLine>();
        foreach (var input in request.Lines)
        {
            var line = entry.Lines.FirstOrDefault(l => l.Id == input.QueueLineId);
            if (line is null)
            {
                return Error.NotFound(nameof(PharmacyQueueLine), input.QueueLineId);
            }

            if (!items.TryGetValue(input.ItemId, out var item))
            {
                return Error.Field("lines", "One of the items is unknown.");
            }

            var prescribed = line.DrugItemId is { } prescribedId ? items.GetValueOrDefault(prescribedId) : null;
            var substitution = SubstitutionPolicy.Evaluate(new SubstitutionCheck(
                line.DrugItemId, line.GenericName ?? prescribed?.GenericName, line.Strength ?? prescribed?.Strength,
                item.Id, item.GenericName, item.Strength, input.SubstitutionReason, input.PrescriberConsent));
            if (substitution.IsFailure)
            {
                return PharmacyLookups.Prefixed(substitution.Error!, line.DrugName);
            }

            planned.Add(new PlannedLine(Guid.CreateVersion7(), line, input, item, substitution.Value));
        }

        var candidates = planned.Select(p => new SaleCandidate(p.Item.Name, p.Item.Schedule, p.Item.IsControlled || p.Line.IsControlledDrug)).ToList();
        var confirmed = SchedulePolicy.CheckControlledConfirmation(candidates, request.ConfirmControlledDrugs);
        if (confirmed.IsFailure)
        {
            return confirmed.Error!;
        }

        var patient = await PharmacyLookups.PatientAsync(db, entry.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), entry.PatientId);
        }

        var prescriber = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == entry.PractitionerId)
            .Select(p => new { p.DisplayName, p.RegistrationNumber })
            .FirstOrDefaultAsync(ct);

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sale = new PharmacySale
        {
            FacilityId = store.Value.FacilityId,
            Type = SaleType.Prescription,
            StoreId = store.Value.Id,
            PatientId = entry.PatientId,
            CustomerName = patient.Name,
            CustomerPhone = patient.Phone,
            QueueEntryId = entry.Id,
            PrescriptionId = entry.PrescriptionId,
            PrescriptionNumber = entry.PrescriptionNumber,
            PractitionerId = entry.PractitionerId,
            PrescriberName = prescriber?.DisplayName,
            PrescriberRegistrationNumber = prescriber?.RegistrationNumber,
            SoldAt = now,
            SoldByUserId = currentUser.UserId,
            CounsellingNotes = request.CounsellingNotes?.Trim(),
            DiscountPercent = request.DiscountPercent,
        };
        sale.InvoiceNumber = await numbers.NextAsync(InvoiceSeries, sale.FacilityId, InvoiceFormat, NumberResetPolicy.Yearly, ct);

        var reference = new StockReference(PharmacyStockReferences.Dispense, sale.Id, sale.InvoiceNumber);
        var issued = await stock.IssueAsync(
            new StockIssueRequest(store.Value.Id, StockMovementType.Dispense, reference,
                planned.Select(p => new StockDemand(p.Item.Id, p.Request.Quantity, p.Request.BatchId, p.Key, Math.Min(p.Line.DurationDays, MaxShelfLifeDays))).ToList()),
            ct);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        foreach (var p in planned)
        {
            foreach (var moved in issued.Value.Movements.Where(m => m.LineId == p.Key))
            {
                var saleItem = PharmacyLookups.SaleItem(sale.Id, p.Item, moved, request.DiscountPercent);
                saleItem.QueueLineId = p.Line.Id;
                saleItem.PrescriptionLineId = p.Line.PrescriptionLineId;
                saleItem.IsSubstitution = p.IsSubstitution;
                saleItem.PrescribedDrugName = p.IsSubstitution ? p.Line.DrugName : null;
                saleItem.SubstitutionReason = p.IsSubstitution ? p.Request.SubstitutionReason?.Trim() : null;
                saleItem.PrescriberConsent = p.IsSubstitution && p.Request.PrescriberConsent;
                sale.Items.Add(saleItem);
            }

            var recorded = entry.RecordDispensed(p.Line.Id, p.Request.Quantity, now);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        var completed = sale.Complete(request.Payments, creditAllowed: true, now, currentUser.UserId);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        sale.MarkDispensed(null);
        db.Add(sale);
        register.Record(sale.FacilityId, issued.Value.Movements, StockMovementType.Dispense, inward: false, reference,
            PharmacyLookups.Party(patient, null, sale.PrescriberName, sale.PrescriberRegistrationNumber, sale.PrescriptionNumber), items, now);

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        await realtime.QueueChangedAsync(entry.FacilityId, entry.Id, entry.Status, ct);
        return Created(sale);
    }

    /// <summary>
    /// Counter sale (PH-02): OTC medicines freely; Schedule H/H1/X only against an outside prescription (and H1/X
    /// only with the buyer's identity for the register). Credit is available to registered patients only.
    /// </summary>
    public async Task<Result<PharmacySaleCreated>> SellOverCounterAsync(CounterSaleRequest request, CancellationToken ct)
    {
        var store = await PharmacyLookups.PharmacyStoreAsync(db, request.StoreId, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        var discount = await CheckDiscountAsync(store.Value.FacilityId, request.DiscountPercent, ct);
        if (discount.IsFailure)
        {
            return discount.Error!;
        }

        PatientSnapshot? patient = null;
        if (request.PatientId is { } patientId)
        {
            var access = await guard.EnsureCanOpenAsync(patientId, ct);
            if (access.IsFailure)
            {
                return access.Error!;
            }

            patient = await PharmacyLookups.PatientAsync(db, patientId, ct);
            if (patient is null)
            {
                return Error.NotFound(nameof(Patient), patientId);
            }
        }

        var items = await InventoryQueries.ItemSummariesAsync(db, request.Lines.Select(l => l.ItemId), ct);
        if (request.Lines.Any(l => !items.ContainsKey(l.ItemId)))
        {
            return Error.Field("lines", "One of the items is unknown.");
        }

        var candidates = request.Lines.Select(l => items[l.ItemId]).Select(i => new SaleCandidate(i.Name, i.Schedule, i.IsControlled)).ToList();
        var customerName = patient?.Name ?? request.CustomerName?.Trim();
        var allowed = SchedulePolicy.CheckCounterSale(candidates, request.Prescription is not null, !string.IsNullOrWhiteSpace(customerName));
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var confirmed = SchedulePolicy.CheckControlledConfirmation(candidates, request.ConfirmControlledDrugs);
        if (confirmed.IsFailure)
        {
            return confirmed.Error!;
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sale = new PharmacySale
        {
            FacilityId = store.Value.FacilityId,
            Type = SaleType.Counter,
            StoreId = store.Value.Id,
            PatientId = patient?.Id,
            CustomerName = customerName,
            CustomerPhone = patient?.Phone ?? Phone.Normalize(request.CustomerPhone),
            PrescriptionNumber = request.Prescription?.Reference?.Trim(),
            PrescriberName = request.Prescription?.PrescriberName.Trim(),
            PrescriberRegistrationNumber = request.Prescription?.PrescriberRegistrationNumber?.Trim(),
            SoldAt = now,
            SoldByUserId = currentUser.UserId,
            DiscountPercent = request.DiscountPercent,
        };
        sale.InvoiceNumber = await numbers.NextAsync(InvoiceSeries, sale.FacilityId, InvoiceFormat, NumberResetPolicy.Yearly, ct);

        var reference = new StockReference(PharmacyStockReferences.CounterSale, sale.Id, sale.InvoiceNumber);
        var issued = await stock.IssueAsync(
            new StockIssueRequest(store.Value.Id, StockMovementType.Sale, reference,
                request.Lines.Select(l => new StockDemand(l.ItemId, l.Quantity, l.BatchId)).ToList()),
            ct);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        foreach (var moved in issued.Value.Movements)
        {
            sale.Items.Add(PharmacyLookups.SaleItem(sale.Id, items[moved.ItemId], moved, request.DiscountPercent));
        }

        var completed = sale.Complete(request.Payments, creditAllowed: patient is not null, now, currentUser.UserId);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        sale.MarkDispensed(null);
        db.Add(sale);
        register.Record(sale.FacilityId, issued.Value.Movements, StockMovementType.Sale, inward: false, reference,
            PharmacyLookups.Party(patient, customerName, sale.PrescriberName, sale.PrescriberRegistrationNumber, sale.PrescriptionNumber), items, now);

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return Created(sale);
    }

    /// <summary>Discounts above the facility's limit (setting <c>pharmacy.discount.max_percent</c>, default 10%) are refused.</summary>
    private async Task<Result> CheckDiscountAsync(Guid facilityId, decimal percent, CancellationToken ct)
    {
        if (percent <= 0)
        {
            return Result.Success();
        }

        var limit = await settings.GetAsync<decimal?>(MaxDiscountSetting, facilityId, ct) ?? DefaultMaxDiscountPercent;
        return percent <= limit
            ? Result.Success()
            : Error.Rule("pharmacy.discount_limit", $"The pharmacy discount limit is {StockErrors.Format(limit)}%.");
    }

    private static PharmacySaleCreated Created(PharmacySale sale) =>
        new(sale.Id, sale.InvoiceNumber, sale.NetAmount, sale.PaidAmount, sale.DueAmount, sale.Status);
}
