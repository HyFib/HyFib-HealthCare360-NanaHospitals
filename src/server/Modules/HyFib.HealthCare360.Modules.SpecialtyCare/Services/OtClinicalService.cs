using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Clinical OT documentation (OT-02..06): pre-op and WHO checklists, anaesthesia record, operation note and
/// implants/consumables. Implants and consumables taken from the OT store are issued from stock (the given lot, else
/// FEFO) and charged in the same transaction, so stock, bill and implant traceability always agree; removing a
/// wrongly entered item before the operation returns the stock and withdraws the charge. Once the operation has started a
/// line is corrected, not removed, and a second person approves the correction (maker-checker slice 10). The swab, needle
/// and instrument counts are made by two members of the team, and a count that does not tally is settled before the Sign-out.
/// </summary>
public sealed class OtClinicalService(
    HospitalDbContext db,
    SpecialtyChargeService charges,
    StockService stock,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    IPermissionService permissions,
    IWitnessConfirmations witnesses,
    IIncidentRegister incidents,
    IApprovals approvals,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>Stock ledger reference module for OT material issues.</summary>
    internal const string StockSource = "specialtycare.ot";

    /// <summary>A correction of an implant or consumable line after the operation started, decided by the OT in-charge.</summary>
    public const string CorrectionKind = "ot.material_correction";

    /// <summary>A swab, needle or instrument count: the second member of the team witnesses it at the counter's screen.</summary>
    public static readonly WitnessPurpose CountWitness = new("ot.count", SpecialtyCarePermissions.OtChecklists, "a swab, needle or instrument count in the theatre");

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

        // A surgeon records the site mark they made, so the record names who marked it.
        var canMarkSite = currentUser.UserId is { } user && await permissions.HasPermissionAsync(user, SpecialtyCarePermissions.OtSurgery, ct);
        var result = booking.RecordPreOp(request.Items, currentUser.Stamp(time.GetUtcNow()), canMarkSite);
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

        if (!fromStore && SurgeryMaterial.ValidateConsignment(request.Kind, request.Brand, request.Manufacturer, request.Mrp, request.CeilingPrice) is { IsFailure: true } consignment)
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
            CeilingPrice = request.CeilingPrice,
            RecordedByName = SignerName(),
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(material);

        // Consignment or patient-supplied items have no SupplyItemId and are not issued from the OT store.
        var issued = await IssueFromStoreAsync(booking, material, ct);
        if (issued.IsFailure)
        {
            return issued.Error!;
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

    /// <summary>
    /// Removing a wrongly entered item before the operation starts also withdraws its charge; if Billing refuses (bill finalised)
    /// the item stays. After it has started the line is corrected instead (<see cref="RequestCorrectionAsync"/>).
    /// </summary>
    public async Task<Result> RemoveMaterialAsync(Guid bookingId, Guid materialId, CancellationToken ct)
    {
        var material = await db.Set<SurgeryMaterial>().FirstOrDefaultAsync(m => m.Id == materialId && m.SurgeryBookingId == bookingId, ct);
        if (material is null)
        {
            return Error.NotFound(nameof(SurgeryMaterial), materialId);
        }

        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstAsync(b => b.Id == bookingId, ct);
        if (booking.MaterialsLocked || material.IsSuperseded || material.CorrectsMaterialId is not null)
        {
            return Error.Rule("surgery.material_locked",
                $"The operation has started, so {material.ItemName} is corrected, not removed: ask for a correction (a quantity of 0 if it was not used), which the OT in-charge approves.");
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

    /// <summary>
    /// Issues an OT store item from stock (the lot written, else FEFO) and records on the line what the item master and the
    /// stock issued say of it. Consignment or patient-supplied items have no item id and are not issued.
    /// </summary>
    private async Task<Result> IssueFromStoreAsync(SurgeryBooking booking, SurgeryMaterial material, CancellationToken ct)
    {
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

            // The bill and a recall name the batch the store issued, also when none was written and FEFO chose it. A line
            // names one batch, so that a recall by lot finds every patient: packs from several are recorded a line each.
            var batches = issued.Value.Movements.Select(m => m.BatchNumber).Where(b => b != StockService.NoBatch).Distinct().ToList();
            if (material.LotNumber is null && batches.Count > 1)
            {
                return Error.Rule(
                    "surgery.material_batches",
                    $"The OT store would issue {material.ItemName} from {batches.Count} batches ({string.Join(", ", batches)}). Record each batch on its own line, with its lot, so that a recall by lot finds this patient.");
            }

            material.LotNumber ??= batches.FirstOrDefault();

            // A unit is charged at most the lowest MRP among the packs issued.
            var mrps = issued.Value.Movements.Where(m => m.Mrp > 0).Select(m => m.Mrp).ToList();
            material.Mrp = mrps.Count == 0 ? null : mrps.Min();
        }

        return Result.Success();
    }

    /// <summary>
    /// Records one count by the signed-in member of the team, witnessed by a second at the same screen (<see cref="CountWitness"/>).
    /// A final count that does not tally opens a discrepancy, audited at Warning, and the surgeon and anaesthetist are told.
    /// </summary>
    public async Task<Result> RecordCountAsync(Guid bookingId, RecordSurgicalCountRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        var now = time.GetUtcNow();
        var countId = Guid.CreateVersion7();
        var allowed = SurgicalCounts.CanRecord(booking.Status, booking.SignOutCompletedAt is not null, booking.Counts, request.Category, request.Stage, request.Number);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var witness = await witnesses.UseAsync(request.WitnessConfirmationId, CountWitness.Key, booking.PatientId, nameof(SurgeryBooking), booking.Id, ct);
        if (witness.IsFailure)
        {
            return witness.Error!;
        }

        var by = currentUser.Stamp(now);
        var recorded = booking.RecordCount(new SurgicalCount(countId, request.Category, request.Stage, request.Number, by.UserId, by.Name,
            witness.Value.UserId, witness.Value.Name, now, Clean(request.Remarks)));
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        if (recorded.Value is { } discrepancy)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "surgery.count_discrepancy",
                $"Surgery {booking.BookingNumber}: final count of {SurgicalCounts.Describe(discrepancy.Category)} {discrepancy.Counted}, expected {discrepancy.Expected} "
                + $"(counted by {by.Name}, witnessed by {witness.Value.Name}). The Sign-out waits until it is settled.")
            {
                PatientId = booking.PatientId,
                EntityType = nameof(SurgeryBooking),
                EntityId = booking.Id.ToString(),
                Severity = AuditSeverity.Warning,
                Details = new { discrepancy.Id, discrepancy.Category, discrepancy.Expected, discrepancy.Counted, witness = witness.Value.UserId },
            });
        }

        return await SaveAsync(booking, Result.Success(), ct);
    }

    /// <summary>
    /// Settles a count that did not tally. A retained item is raised in the Quality incident register as a never event
    /// (sentinel, root-cause analysis due), in the same transaction, and the discrepancy names the incident.
    /// </summary>
    public async Task<Result<CountDiscrepancyResolved>> ResolveDiscrepancyAsync(Guid bookingId, Guid discrepancyId, ResolveCountDiscrepancyRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        var open = booking.CountDiscrepancies.FirstOrDefault(d => d.Id == discrepancyId);
        if (open is null)
        {
            return Error.NotFound(nameof(CountDiscrepancy), discrepancyId);
        }

        if (!open.IsOpen)
        {
            return Error.Rule("surgery.discrepancy_settled", $"The discrepancy in the {SurgicalCounts.Describe(open.Category)} count is already settled.");
        }

        var now = time.GetUtcNow();
        var by = currentUser.Stamp(now);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        (Guid, string)? incident = null;
        if (request.Resolution == DiscrepancyResolution.RetainedItem)
        {
            var theatre = booking.TheatreId is { } theatreId
                ? await db.Set<OperationTheatre>().AsNoTracking().Where(t => t.Id == theatreId).Select(t => t.Name).FirstOrDefaultAsync(ct)
                : null;
            var raised = await incidents.RaiseAsync(new PatientSafetyIncident(
                booking.FacilityId,
                booking.PatientId,
                NeverEvent.RetainedForeignObject,
                request.Harm ?? IncidentHarm.Moderate,
                booking.StartedAt ?? now,
                theatre ?? "Operation theatre",
                $"Retained {SurgicalCounts.Describe(open.Category)} after surgery {booking.BookingNumber} ({booking.Site.Words}): final count {open.Counted}, expected {open.Expected}. {request.Note.Trim()}",
                string.IsNullOrWhiteSpace(request.XrayResult) ? null : $"X-ray: {request.XrayResult.Trim()}",
                InvolvesDevice: open.Category != CountCategory.Swabs), ct);
            if (raised.IsFailure)
            {
                return raised.Error!;
            }

            incident = (raised.Value.IncidentId, raised.Value.IncidentNumber);
        }

        var settled = booking.ResolveDiscrepancy(discrepancyId, request.Resolution, request.Note, request.XrayResult, incident, by);
        if (settled.IsFailure)
        {
            return settled.Error!;
        }

        var resolution = settled.Value;
        audit.Add(new AuditEntry(AuditCategories.Clinical, "surgery.count_discrepancy_settled",
            Fit($"Surgery {booking.BookingNumber}: the {SurgicalCounts.Describe(resolution.Category)} count ({resolution.Counted}, expected {resolution.Expected}) settled as "
                + $"{SurgicalCounts.Describe(request.Resolution)} by {by.Name}: {resolution.ResolutionNote}"
                + (resolution.IncidentNumber is { } number ? $"; incident {number} raised" : string.Empty), 1000))
        {
            PatientId = booking.PatientId,
            EntityType = nameof(SurgeryBooking),
            EntityId = booking.Id.ToString(),
            Severity = request.Resolution == DiscrepancyResolution.RetainedItem ? AuditSeverity.Critical : AuditSeverity.Warning,
            Details = new { resolution.Id, resolution.Category, resolution.Resolution, resolution.XrayResult, resolution.IncidentId },
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return new CountDiscrepancyResolved(resolution.Id, request.Resolution, resolution.IncidentId, resolution.IncidentNumber);
    }

    /// <summary>
    /// Asks for a correction of an implant or consumable line once the operation has started: the line stays as it is until the
    /// OT in-charge (<see cref="SpecialtyCarePermissions.OtMaterialsApprove"/>), never the one who asked, approves it.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> RequestCorrectionAsync(Guid bookingId, Guid materialId, CorrectSurgeryMaterialRequest request, CancellationToken ct)
    {
        var material = await db.Set<SurgeryMaterial>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == materialId && m.SurgeryBookingId == bookingId, ct);
        if (material is null)
        {
            return Error.NotFound(nameof(SurgeryMaterial), materialId);
        }

        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstAsync(b => b.Id == bookingId, ct);
        if (!booking.MaterialsLocked)
        {
            return Error.Rule("surgery.material_not_locked", "Before the operation starts, remove the line and record it again.");
        }

        if (material.IsSuperseded)
        {
            return Error.Rule("surgery.material_superseded", $"{material.ItemName} was already corrected; correct the line that replaced it.");
        }

        var before = MaterialFacts.Of(material);
        var asked = new MaterialFacts(Clean(request.LotNumber)?.ToUpperInvariant(), Clean(request.SerialNumber)?.ToUpperInvariant(), request.ExpiryDate, request.Quantity);
        if (before == asked)
        {
            return Error.Field("quantity", "Nothing is corrected: change the batch, serial number, expiry or quantity.");
        }

        if (asked.Quantity > 0)
        {
            var valid = SurgeryMaterial.Validate(material.Kind, asked.LotNumber, asked.SerialNumber, asked.ExpiryDate, time.Today(), material.SupplyItemId is not null);
            if (valid.IsFailure)
            {
                return valid.Error!;
            }
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == material.PatientId).Select(p => new { p.Uhid, p.FullName, p.IsVip }).FirstAsync(ct);
        var words = MaterialFacts.Change(before, asked);
        List<ApprovalDetail> details =
        [
            new("Surgery", $"{booking.BookingNumber} · {patient.Uhid} · {(patient.IsVip ? PatientNames.Restricted(patient.FullName) : patient.FullName)}"),
            new(material.Kind == MaterialKind.Implant ? "Implant" : "Consumable", material.ItemName),
            new("Correction", words),
            new("Recorded by", $"{material.RecordedByName} at {material.CreatedAt:dd MMM yyyy HH:mm} UTC"),
            new("What it reaches", asked.Quantity == 0
                ? $"The line is kept as not used: its charge of ₹{Money.FormatInr(material.Amount)} is withdrawn{(material.SupplyItemId is null ? string.Empty : " and the stock goes back to the OT store")}."
                : $"The line is kept as corrected and a corrected line replaces it: the charge is withdrawn and posted again (₹{Money.FormatInr(material.Amount)} → ₹{Money.FormatInr(Money.Round(asked.Quantity * material.UnitPrice))})"
                  + (material.SupplyItemId is null ? "." : ", and the stock is returned and issued again from the corrected batch.")),
        ];

        return await approvals.RequestAsync(
            CorrectionKind,
            new ApprovalSubject(nameof(SurgeryMaterial), material.Id, material.Version, material.FacilityId, material.PatientId),
            new MaterialCorrectionPayload(material.Id, booking.Id, before, asked),
            $"{material.ItemName} on surgery {booking.BookingNumber}: {words}",
            details,
            request.Reason,
            ct);
    }

    /// <summary>
    /// Makes an approved correction: the line is kept, superseded; its charge is withdrawn and its stock returned; a line with
    /// the corrected values is issued and charged in its place (none when the item was not used). Made only over the line as it
    /// was asked (<c>approval.stale</c>), audited with both names. A charge already on a final bill cannot be withdrawn: the
    /// correction then fails and a credit note is asked for instead.
    /// </summary>
    internal async Task<Result<string>> ApplyCorrectionAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = ApprovalPayload.Read<MaterialCorrectionPayload>(request.Payload);
        var material = await db.Set<SurgeryMaterial>().FirstOrDefaultAsync(m => m.Id == payload.MaterialId, ct);
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == payload.BookingId, ct);
        if (material is null || booking is null)
        {
            return Error.Rule("surgery.material_gone", "The line no longer exists, so nothing was changed.");
        }

        if (material.IsSuperseded || MaterialFacts.Of(material) != payload.Before)
        {
            return Error.Rule("approval.stale", $"{material.ItemName} changed after this was asked for, so nothing was changed. Ask again.");
        }

        var approver = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        var reason = $"Corrected on surgery {booking.BookingNumber}: {request.Reason}";
        if (material.ChargeStatus == ChargeStatus.Posted)
        {
            var cancelled = await charges.CancelAsync(ChargeSources.Ot, MaterialReference(material), Fit(reason, 500), ct);
            if (cancelled.Status == ChargeStatus.Failed)
            {
                return Error.Rule("surgery.charge_cancel_failed", $"The charge could not be withdrawn: {cancelled.Error}");
            }
        }

        if (material.SupplyItemId is not null)
        {
            var returned = await stock.ReverseConsumptionAsync(new StockReference(StockSource, material.Id, booking.BookingNumber, Fit(reason, 200)), ct);
            if (returned.IsFailure)
            {
                return returned.Error!;
            }
        }

        var now = time.GetUtcNow();
        material.SupersededAt = now;
        material.ChargeStatus = material.ChargeStatus == ChargeStatus.Posted ? ChargeStatus.Cancelled : material.ChargeStatus;
        material.CorrectionReason = Fit(request.Reason, 500);
        material.CorrectionRequestedByName = request.RequestedByName;
        material.CorrectionApprovedByName = approver;

        SurgeryMaterial? replacement = null;
        if (payload.Asked.Quantity > 0)
        {
            replacement = new SurgeryMaterial
            {
                FacilityId = material.FacilityId,
                SurgeryBookingId = material.SurgeryBookingId,
                PatientId = material.PatientId,
                Kind = material.Kind,
                SupplyItemId = material.SupplyItemId,
                ItemName = material.ItemName,
                Brand = material.Brand,
                Manufacturer = material.Manufacturer,
                Specification = material.Specification,
                LotNumber = payload.Asked.LotNumber,
                SerialNumber = payload.Asked.SerialNumber,
                ExpiryDate = payload.Asked.ExpiryDate,
                Quantity = payload.Asked.Quantity,
                UnitPrice = material.UnitPrice,
                Mrp = material.Mrp,
                CeilingPrice = material.CeilingPrice,
                RecordedByName = request.RequestedByName,
                CorrectsMaterialId = material.Id,
            };
            db.Add(replacement);
            var issued = await IssueFromStoreAsync(booking, replacement, ct);
            if (issued.IsFailure)
            {
                return issued.Error!;
            }

            var priced = replacement.CheckPrice();
            if (priced.IsFailure)
            {
                return priced.Error!;
            }

            var outcome = await PostMaterialChargeAsync(booking, replacement, ct);
            if (outcome.Status == ChargeStatus.Failed)
            {
                return Error.Rule("surgery.charge_post_failed", $"The corrected charge could not be posted: {outcome.Error}");
            }

            replacement.ChargeStatus = outcome.Status;
            material.SupersededByMaterialId = replacement.Id;
        }

        var words = MaterialFacts.Change(payload.Before, payload.Asked);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "ot.material_corrected",
            Fit($"{material.ItemName} on surgery {booking.BookingNumber}: {words}: {request.Reason} (asked for by {request.RequestedByName}, approved by {approver})", 1000))
        {
            PatientId = material.PatientId,
            EntityType = nameof(SurgeryMaterial),
            EntityId = material.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new
            {
                before = payload.Before,
                after = payload.Asked,
                replacement = replacement?.Id,
                approval = request.Id,
                requestedBy = request.RequestedBy,
                approvedBy = currentUser.UserId,
            },
        });

        var saved = await stock.SaveAsync(ct);
        return saved.IsFailure ? saved.Error! : words;
    }

    /// <summary>The corrections waiting for approval on these lines, by line.</summary>
    internal async Task<IReadOnlyDictionary<Guid, PendingMaterialCorrection>> PendingCorrectionsAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken ct)
    {
        if (materialIds.Count == 0)
        {
            return new Dictionary<Guid, PendingMaterialCorrection>();
        }

        var now = time.GetUtcNow();
        var rows = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == CorrectionKind && r.SubjectType == nameof(SurgeryMaterial) && materialIds.Contains(r.SubjectId)
                        && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .Select(r => new { r.SubjectId, r.Id, r.Summary, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.SubjectId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RequestedAt).Select(r => new PendingMaterialCorrection(r.Id, r.Summary, r.RequestedByName, r.RequestedAt)).First());
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

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    internal static AnaesthesiaRecordDto ToDto(AnaesthesiaRecord r) => new(
        r.Id, r.SurgeryBookingId, r.PatientId, r.AnaesthetistId, r.AsaGrade, r.AsaEmergency, r.Technique, r.Airway, r.AirwayNotes, r.AnaesthesiaStartAt,
        r.AnaesthesiaEndAt, r.Vitals, r.Drugs, r.Fluids, r.EstimatedBloodLossMl, r.UrineOutputMl, r.Complications, r.RecoveryNotes, r.SignedAt,
        r.SignedByName, r.Version);

    internal static OperationNoteDto ToDto(OperationNote n) => new(
        n.Id, n.SurgeryBookingId, n.PatientId, n.SurgeonId, n.PreOperativeDiagnosis, n.PostOperativeDiagnosis, n.ProcedurePerformed, n.Findings,
        n.ProcedureDetails, n.Specimens, n.Drains, n.EstimatedBloodLossMl, n.Complications, n.PostOpOrders, n.SignedAt, n.SignedByName, n.Version);

    internal static SurgeryMaterialDto ToDto(SurgeryMaterial m, PendingMaterialCorrection? pending = null) => new(
        m.Id, m.Kind, m.SupplyItemId, m.ItemName, m.Manufacturer, m.LotNumber, m.SerialNumber, m.ExpiryDate, m.Quantity, m.UnitPrice, m.Amount,
        m.RecordedByName, m.CreatedAt, m.ChargeStatus, m.ChargeError, m.Brand, m.Specification, m.Mrp, m.CeilingPrice, m.CorrectsMaterialId,
        m.SupersededAt, m.SupersededByMaterialId, m.CorrectionReason, m.CorrectionRequestedByName, m.CorrectionApprovedByName, pending);
}
