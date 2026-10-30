using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Clinical OT documentation (OT-02..06): pre-op and WHO checklists, anaesthesia record, operation note and
/// implants/consumables. Implants and consumables taken from the OT store are issued from stock (the given lot, else
/// FEFO) and charged in the same transaction, so stock, bill and implant traceability always agree; removing a
/// wrongly entered item returns the stock and withdraws the charge.
/// </summary>
public sealed class OtClinicalService(
    HospitalDbContext db,
    SpecialtyChargeService charges,
    StockService stock,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>Stock ledger reference module for OT material issues.</summary>
    internal const string StockSource = "specialtycare.ot";

    public async Task<Result> RecordPreOpAsync(Guid bookingId, RecordChecklistRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(booking, version);
        }

        var result = booking.RecordPreOp(request.Items, currentUser.Stamp(time.GetUtcNow()));
        return await SaveAsync(booking, result, ct);
    }

    public async Task<Result> ConfirmSafetyAsync(Guid bookingId, SafetyPhase phase, RecordChecklistRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        var result = booking.ConfirmSafety(phase, request.Items, currentUser.Stamp(time.GetUtcNow()));
        return await SaveAsync(booking, result, ct);
    }

    public async Task<Result<AnaesthesiaRecordDto>> SaveAnaesthesiaAsync(Guid bookingId, SaveAnaesthesiaRecordRequest request, CancellationToken ct)
    {
        var loaded = await LoadAnaesthesiaAsync(bookingId, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var record = loaded.Value;
        var editable = record.EnsureEditable();
        if (editable.IsFailure)
        {
            return editable.Error!;
        }

        db.SetExpectedVersion(record, request.Version);

        record.AsaGrade = request.AsaGrade;
        record.AsaEmergency = request.AsaEmergency;
        record.Technique = request.Technique;
        record.Airway = request.Airway;
        record.AirwayNotes = Clean(request.AirwayNotes);
        record.AnaesthesiaStartAt = request.AnaesthesiaStartAt;
        record.AnaesthesiaEndAt = request.AnaesthesiaEndAt;
        record.Drugs = (request.Drugs ?? []).OrderBy(d => d.At).ToList();
        record.Fluids = (request.Fluids ?? []).OrderBy(f => f.At).ToList();
        record.EstimatedBloodLossMl = request.EstimatedBloodLossMl;
        record.UrineOutputMl = request.UrineOutputMl;
        record.Complications = Clean(request.Complications);
        record.RecoveryNotes = Clean(request.RecoveryNotes);
        record.AnaesthetistId ??= currentUser.PractitionerId;

        if (request.Sign)
        {
            var signed = record.Sign(SignerName(), time.GetUtcNow());
            if (signed.IsFailure)
            {
                return signed.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<Result<AnaesthesiaRecordDto>> AddVitalsAsync(Guid bookingId, AddAnaesthesiaVitalsRequest request, CancellationToken ct)
    {
        var loaded = await LoadAnaesthesiaAsync(bookingId, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var result = loaded.Value.AddVitals(request.Rows);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(loaded.Value);
    }

    public async Task<Result<OperationNoteDto>> SaveOperationNoteAsync(Guid bookingId, SaveOperationNoteRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        if (booking.Status is not (SurgeryStatus.InProgress or SurgeryStatus.Completed))
        {
            return Error.Rule("surgery.not_started", "The operation note is written once surgery has started.");
        }

        var note = await db.Set<OperationNote>().FirstOrDefaultAsync(n => n.SurgeryBookingId == bookingId, ct);
        if (note is null)
        {
            note = new OperationNote { FacilityId = booking.FacilityId, SurgeryBookingId = booking.Id, PatientId = booking.PatientId, SurgeonId = booking.SurgeonId };
            db.Add(note);
        }
        else
        {
            var editable = note.EnsureEditable();
            if (editable.IsFailure)
            {
                return editable.Error!;
            }

            db.SetExpectedVersion(note, request.Version);
        }

        note.PreOperativeDiagnosis = Clean(request.PreOperativeDiagnosis) ?? booking.Diagnosis;
        note.PostOperativeDiagnosis = Clean(request.PostOperativeDiagnosis);
        note.ProcedurePerformed = request.ProcedurePerformed.Trim();
        note.Findings = Clean(request.Findings);
        note.ProcedureDetails = Clean(request.ProcedureDetails);
        note.Specimens = [.. request.Specimens ?? []];
        note.Drains = Clean(request.Drains);
        note.EstimatedBloodLossMl = request.EstimatedBloodLossMl;
        note.Complications = Clean(request.Complications);
        note.PostOpOrders = Clean(request.PostOpOrders);

        if (request.Sign)
        {
            var signed = note.Sign(SignerName(), time.GetUtcNow());
            if (signed.IsFailure)
            {
                return signed.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        return ToDto(note);
    }

    public async Task<Result<SurgeryMaterialDto>> AddMaterialAsync(Guid bookingId, AddSurgeryMaterialRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        if (booking.Status is not (SurgeryStatus.InPreOp or SurgeryStatus.InProgress or SurgeryStatus.Completed))
        {
            return Error.Rule("surgery.materials_closed", "Implants and consumables are recorded from pre-op until the surgery is completed.");
        }

        var fromStore = request.SupplyItemId is not null;
        var valid = SurgeryMaterial.Validate(request.Kind, request.LotNumber, request.SerialNumber, request.ExpiryDate, time.Today(), fromStore);
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        if (!fromStore && SurgeryMaterial.ValidateConsignment(request.Kind, request.Brand, request.Manufacturer, request.Mrp) is { IsFailure: true } consignment)
        {
            return consignment.Error!;
        }

        var material = new SurgeryMaterial
        {
            FacilityId = booking.FacilityId,
            SurgeryBookingId = booking.Id,
            PatientId = booking.PatientId,
            Kind = request.Kind,
            SupplyItemId = request.SupplyItemId,
            ItemName = request.ItemName.Trim(),
            Brand = Clean(request.Brand),
            Manufacturer = Clean(request.Manufacturer),
            Specification = Clean(request.Specification),
            LotNumber = Clean(request.LotNumber)?.ToUpperInvariant(),
            SerialNumber = Clean(request.SerialNumber)?.ToUpperInvariant(),
            ExpiryDate = request.ExpiryDate,
            Quantity = request.Quantity,
            UnitPrice = Money.Round(request.UnitPrice),
            Mrp = request.Mrp,
            RecordedByName = SignerName(),
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(material);

        // Consignment or patient-supplied items have no SupplyItemId and are not issued from the OT store.
        if (material.SupplyItemId is { } itemId)
        {
            // The item master says whose the item is and what it is, as its bill must (NPPA), whatever the dialog sent; the
            // name may carry a size the nurse added.
            var item = await db.Set<Item>().AsNoTracking().Where(i => i.Id == itemId)
                .Select(i => new { i.BrandName, i.Manufacturer, i.Specification, i.CeilingPrice, i.Type })
                .FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return Error.NotFound(nameof(Item), itemId);
            }

            material.Brand = item.BrandName;
            material.Manufacturer = item.Manufacturer;
            material.Specification = item.Specification;
            material.CeilingPrice = item.Type == ItemType.Implant ? item.CeilingPrice : null;
            material.Mrp = null;

            var store = await stock.ResolveStoreAsync(booking.FacilityId, StoreType.OT, ct);
            if (store.IsFailure)
            {
                return store.Error!;
            }

            var issued = await stock.ConsumeAsync(
                new StockConsumption(
                    store.Value,
                    itemId,
                    material.Quantity,
                    new StockReference(StockSource, material.Id, booking.BookingNumber, $"Surgery {booking.BookingNumber}"),
                    material.LotNumber,
                    material.ExpiryDate),
                ct);
            if (issued.IsFailure)
            {
                return issued.Error!;
            }

            // The bill and a recall name the batch the store issued, also when none was written and FEFO chose it; a unit
            // is charged at most the lowest MRP among the packs issued.
            var batches = string.Join(", ", issued.Value.Movements.Select(m => m.BatchNumber).Distinct());
            material.LotNumber ??= batches.Length <= 64 ? batches : batches[..64];
            var mrps = issued.Value.Movements.Where(m => m.Mrp > 0).Select(m => m.Mrp).ToList();
            material.Mrp = mrps.Count == 0 ? null : mrps.Min();
        }

        var priced = material.CheckPrice();
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        var outcome = await PostMaterialChargeAsync(booking, material, ct);
        material.ChargeStatus = outcome.Status;
        material.ChargeError = outcome.Error;
        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return ToDto(material);
    }

    /// <summary>Removing a wrongly entered item also withdraws its charge; if Billing refuses (bill finalised) the item stays.</summary>
    public async Task<Result> RemoveMaterialAsync(Guid bookingId, Guid materialId, CancellationToken ct)
    {
        var material = await db.Set<SurgeryMaterial>().FirstOrDefaultAsync(m => m.Id == materialId && m.SurgeryBookingId == bookingId, ct);
        if (material is null)
        {
            return Error.NotFound(nameof(SurgeryMaterial), materialId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (material.ChargeStatus == ChargeStatus.Posted)
        {
            var cancelled = await charges.CancelAsync(ChargeSources.Ot, MaterialReference(material), "Entry removed from the surgery record", ct);
            if (cancelled.Status == ChargeStatus.Failed)
            {
                return Error.Rule("surgery.charge_cancel_failed", $"The charge could not be withdrawn: {cancelled.Error}");
            }
        }

        if (material.SupplyItemId is not null)
        {
            var returned = await stock.ReverseConsumptionAsync(new StockReference(StockSource, material.Id, null, "Entry removed from the surgery record"), ct);
            if (returned.IsFailure)
            {
                return returned.Error!;
            }
        }

        db.Remove(material);
        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    /// <summary>Re-posts material charges that failed earlier (idempotent).</summary>
    public async Task<ChargeOutcome> RetryMaterialChargesAsync(Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        var pending = await db.Set<SurgeryMaterial>().Where(m => m.SurgeryBookingId == bookingId && m.ChargeStatus == ChargeStatus.Failed).ToListAsync(ct);
        if (booking is null || pending.Count == 0)
        {
            return ChargeOutcome.Nothing;
        }

        var last = ChargeOutcome.Posted;
        foreach (var material in pending)
        {
            var outcome = await PostMaterialChargeAsync(booking, material, ct);
            material.ChargeStatus = outcome.Status;
            material.ChargeError = outcome.Error;
            last = outcome.Status == ChargeStatus.Failed ? outcome : last;
        }

        await db.SaveChangesAsync(ct);
        return last;
    }

    // The line names the item; its brand, maker, batch, serial, specification and MRP print beneath it (NPPA).
    private Task<ChargeOutcome> PostMaterialChargeAsync(SurgeryBooking booking, SurgeryMaterial material, CancellationToken ct)
    {
        var description = material.Kind == MaterialKind.Implant ? $"Implant: {material.ItemName}" : $"OT consumable: {material.ItemName}";

        return charges.PostAsync(
        [
            new ChargeLine(ChargeSources.Ot, MaterialReference(material), booking.PatientId, booking.VisitId, booking.AdmissionId,
                material.Kind == MaterialKind.Implant ? OtServiceCodes.Implant : OtServiceCodes.Consumable,
                Quantity: material.Quantity, UnitPriceOverride: material.UnitPrice, Description: description, ServiceDate: time.GetUtcNow(),
                Device: material.Device),
        ], ct);
    }

    private async Task<Result<AnaesthesiaRecord>> LoadAnaesthesiaAsync(Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        if (booking.Status is SurgeryStatus.Cancelled or SurgeryStatus.Requested)
        {
            return Error.Rule("surgery.not_scheduled", "The anaesthesia record is kept for scheduled surgeries.");
        }

        var record = await db.Set<AnaesthesiaRecord>().FirstOrDefaultAsync(r => r.SurgeryBookingId == bookingId, ct);
        if (record is null)
        {
            record = new AnaesthesiaRecord
            {
                FacilityId = booking.FacilityId,
                SurgeryBookingId = booking.Id,
                PatientId = booking.PatientId,
                AnaesthetistId = booking.AnaesthetistId,
                Technique = booking.AnaesthesiaType,
            };
            db.Add(record);
        }

        return record;
    }

    private async Task<Result> SaveAsync(SurgeryBooking booking, Result result, CancellationToken ct)
    {
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return Result.Success();
    }

    private string SignerName() => currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";

    private static string MaterialReference(SurgeryMaterial material) => $"{material.SurgeryBookingId}:material:{material.Id}";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static AnaesthesiaRecordDto ToDto(AnaesthesiaRecord r) => new(
        r.Id, r.SurgeryBookingId, r.PatientId, r.AnaesthetistId, r.AsaGrade, r.AsaEmergency, r.Technique, r.Airway, r.AirwayNotes, r.AnaesthesiaStartAt,
        r.AnaesthesiaEndAt, r.Vitals, r.Drugs, r.Fluids, r.EstimatedBloodLossMl, r.UrineOutputMl, r.Complications, r.RecoveryNotes, r.SignedAt,
        r.SignedByName, r.Version);

    internal static OperationNoteDto ToDto(OperationNote n) => new(
        n.Id, n.SurgeryBookingId, n.PatientId, n.SurgeonId, n.PreOperativeDiagnosis, n.PostOperativeDiagnosis, n.ProcedurePerformed, n.Findings,
        n.ProcedureDetails, n.Specimens, n.Drains, n.EstimatedBloodLossMl, n.Complications, n.PostOpOrders, n.SignedAt, n.SignedByName, n.Version);

    internal static SurgeryMaterialDto ToDto(SurgeryMaterial m) => new(
        m.Id, m.Kind, m.SupplyItemId, m.ItemName, m.Manufacturer, m.LotNumber, m.SerialNumber, m.ExpiryDate, m.Quantity, m.UnitPrice, m.Amount,
        m.RecordedByName, m.CreatedAt, m.ChargeStatus, m.ChargeError, m.Brand, m.Specification, m.Mrp, m.CeilingPrice);
}
