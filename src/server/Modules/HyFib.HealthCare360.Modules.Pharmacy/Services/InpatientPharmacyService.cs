using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// IP pharmacy (PH-03): nurses raise ward indents per patient; the IP pharmacist issues against the indent, the
/// in-patient prescription or directly. Each issued line is charged to the admission through
/// <see cref="IChargePoster"/> at MRP (SourceModule "pharmacy.ip", SourceReference = issue line id, so retries
/// never double-charge), feeds the MAR through <see cref="MedicationDispensed"/>, and returns from the ward
/// restock the batch and reverse the charge (cancel the untouched line, otherwise a negative credit line).
/// </summary>
public sealed class InpatientPharmacyService(
    HospitalDbContext db,
    INumberSeries numbers,
    StockService stock,
    IChargePoster charges,
    ControlledDrugRegisterService register,
    PharmacyRealtime realtime,
    PatientAccessGuard guard,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string ChargeModule = "pharmacy.ip";
    public const string ChargeReturnModule = "pharmacy.ip.return";
    public const string MedicinesServiceCode = "PHARM-MED";
    public const string ConsumablesServiceCode = "PHARM-CONS";
    public const string ImplantsServiceCode = "PHARM-IMPL";

    private sealed record PlannedLine(Guid Key, IpIssueLineInput Input, ItemSummary Item, WardIndentLine? IndentLine, PharmacyQueueLine? QueueLine);

    /// <summary>Which catalogue service an item is billed under on the IP bill.</summary>
    public static string ServiceCodeFor(ItemType type) => type switch
    {
        ItemType.Drug => MedicinesServiceCode,
        ItemType.Implant => ImplantsServiceCode,
        _ => ConsumablesServiceCode,
    };

    // ------------------------------------------------------------------ ward indents

    public async Task<Result<PharmacyDocumentCreated>> RaiseIndentAsync(RaiseWardIndentRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var access = await guard.EnsureCanOpenAsync(request.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var items = await InventoryQueries.ItemSummariesAsync(db, request.Lines.Select(l => l.ItemId), ct);
        if (request.Lines.Any(l => !items.TryGetValue(l.ItemId, out var item) || !item.IsActive))
        {
            return Error.Field("lines", "One of the items is unknown or inactive.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var indent = new WardIndent
        {
            FacilityId = facilityId,
            Number = await numbers.NextAsync(PharmacyNumberSeries.WardIndent, facilityId, ct),
            PatientId = request.PatientId,
            AdmissionId = request.AdmissionId,
            VisitId = request.VisitId,
            WardName = request.WardName?.Trim(),
            BedLabel = request.BedLabel?.Trim(),
            Urgency = request.Urgency,
            RaisedByUserId = currentUser.UserId,
            RaisedAt = time.GetUtcNow(),
            PrescriptionId = request.PrescriptionId,
            Remarks = request.Remarks?.Trim(),
        };
        indent.Lines.AddRange(request.Lines.Select(l => new WardIndentLine
        {
            WardIndentId = indent.Id,
            ItemId = l.ItemId,
            ItemName = items[l.ItemId].Name,
            RequestedQuantity = l.Quantity,
            Remarks = l.Remarks?.Trim(),
        }));
        db.Add(indent);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.WardIndentChangedAsync(facilityId, indent.Id, indent.Status, ct);
        return new PharmacyDocumentCreated(indent.Id, indent.Number);
    }

    public async Task<Result> CancelIndentAsync(Guid indentId, string reason, CancellationToken ct)
    {
        var indent = await db.Set<WardIndent>().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == indentId, ct);
        if (indent is null)
        {
            return Error.NotFound(nameof(WardIndent), indentId);
        }

        var cancelled = indent.Cancel(reason.Trim());
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await db.SaveChangesAsync(ct);
        await realtime.WardIndentChangedAsync(indent.FacilityId, indent.Id, indent.Status, ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ issues

    public async Task<Result<IpIssueCreated>> IssueAsync(IpIssueRequest request, CancellationToken ct)
    {
        var store = await PharmacyLookups.PharmacyStoreAsync(db, request.StoreId, ct);
        if (store.IsFailure)
        {
            return store.Error!;
        }

        WardIndent? indent = null;
        PharmacyQueueEntry? entry = null;
        Guid patientId;
        Guid admissionId;
        Guid? visitId;
        Guid? prescriptionId = null;
        if (request.WardIndentId is { } indentId)
        {
            indent = await db.Set<WardIndent>().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == indentId, ct);
            if (indent is null)
            {
                return Error.NotFound(nameof(WardIndent), indentId);
            }

            if (!indent.IsOpen)
            {
                return Error.Rule("ward_indent.closed", $"Ward indent {indent.Number} is {indent.Status}.");
            }

            (patientId, admissionId, visitId, prescriptionId) = (indent.PatientId, indent.AdmissionId, indent.VisitId, indent.PrescriptionId);
        }
        else if (request.QueueEntryId is { } entryId)
        {
            entry = await db.Set<PharmacyQueueEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, ct);
            if (entry is null)
            {
                return Error.NotFound(nameof(PharmacyQueueEntry), entryId);
            }

            if (!entry.IsInpatient || entry.AdmissionId is null)
            {
                return Error.Rule("pharmacy.outpatient_prescription", "Out-patient prescriptions are dispensed at the OP counter.");
            }

            if (!entry.IsOpen)
            {
                return Error.Rule("queue.closed", $"Prescription {entry.PrescriptionNumber} is {entry.Status}.");
            }

            (patientId, admissionId, visitId, prescriptionId) = (entry.PatientId, entry.AdmissionId.Value, entry.VisitId, entry.PrescriptionId);
        }
        else if (request.PatientId is { } directPatient && request.AdmissionId is { } directAdmission)
        {
            (patientId, admissionId, visitId) = (directPatient, directAdmission, request.VisitId);
        }
        else
        {
            return Error.Field("admissionId", "Choose the admitted patient.");
        }

        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var itemIds = request.Lines.Select(l => l.ItemId)
            .Concat(entry?.Lines.Where(l => l.DrugItemId != null).Select(l => l.DrugItemId!.Value) ?? []);
        var items = await InventoryQueries.ItemSummariesAsync(db, itemIds, ct);
        var planned = new List<PlannedLine>();
        foreach (var input in request.Lines)
        {
            if (!items.TryGetValue(input.ItemId, out var item))
            {
                return Error.Field("lines", "One of the items is unknown.");
            }

            WardIndentLine? indentLine = null;
            if (indent is not null)
            {
                indentLine = indent.Lines.FirstOrDefault(l => l.Id == input.WardIndentLineId && l.ItemId == item.Id);
                if (indentLine is null)
                {
                    return Error.Rule("ward_indent.line_mismatch", $"{item.Name} is not on ward indent {indent.Number}.");
                }
            }

            PharmacyQueueLine? queueLine = null;
            if (entry is not null)
            {
                queueLine = entry.Lines.FirstOrDefault(l => l.Id == input.QueueLineId);
                if (queueLine is null)
                {
                    return Error.Rule("queue.line_mismatch", $"Choose which prescription line {item.Name} is issued for.");
                }

                var prescribed = queueLine.DrugItemId is { } prescribedId ? items.GetValueOrDefault(prescribedId) : null;
                var substitution = SubstitutionPolicy.Evaluate(new SubstitutionCheck(
                    queueLine.DrugItemId, queueLine.GenericName ?? prescribed?.GenericName, queueLine.Strength ?? prescribed?.Strength,
                    item.Id, item.GenericName, item.Strength, input.SubstitutionReason, input.PrescriberConsent));
                if (substitution.IsFailure)
                {
                    return PharmacyLookups.Prefixed(substitution.Error!, queueLine.DrugName);
                }
            }

            planned.Add(new PlannedLine(Guid.CreateVersion7(), input, item, indentLine, queueLine));
        }

        var confirmed = SchedulePolicy.CheckControlledConfirmation(
            planned.Select(p => new SaleCandidate(p.Item.Name, p.Item.Schedule, p.Item.IsControlled || p.QueueLine?.IsControlledDrug == true)).ToList(),
            request.ConfirmControlledDrugs);
        if (confirmed.IsFailure)
        {
            return confirmed.Error!;
        }

        var services = await ServiceItemsAsync(ct);
        if (services.IsFailure)
        {
            return services.Error!;
        }

        var patient = await PharmacyLookups.PatientAsync(db, patientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var issue = new IpIssue
        {
            FacilityId = store.Value.FacilityId,
            Number = await numbers.NextAsync(PharmacyNumberSeries.WardIssue, store.Value.FacilityId, ct),
            StoreId = store.Value.Id,
            PatientId = patientId,
            AdmissionId = admissionId,
            VisitId = visitId,
            WardIndentId = indent?.Id,
            QueueEntryId = entry?.Id,
            PrescriptionId = prescriptionId,
            IssuedAt = now,
            IssuedByUserId = currentUser.UserId,
        };
        var reference = new StockReference(PharmacyStockReferences.IpIssue, issue.Id, issue.Number);
        var issued = await stock.IssueAsync(
            new StockIssueRequest(store.Value.Id, StockMovementType.Dispense, reference,
                planned.Select(p => new StockDemand(p.Item.Id, p.Input.Quantity, p.Input.BatchId, p.Key)).ToList()),
            ct);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        foreach (var p in planned)
        {
            foreach (var moved in issued.Value.Movements.Where(m => m.LineId == p.Key))
            {
                issue.Lines.Add(new IpIssueLine
                {
                    IpIssueId = issue.Id,
                    ItemId = p.Item.Id,
                    ItemName = p.Item.Name,
                    ItemType = p.Item.Type,
                    Schedule = p.Item.Schedule,
                    IsControlled = p.Item.IsControlled,
                    BatchId = moved.BatchId,
                    BatchNumber = moved.BatchNumber,
                    ExpiryDate = moved.ExpiryDate,
                    Quantity = moved.Quantity,
                    UnitMrp = moved.Mrp,
                    Amount = Money.Round(moved.Quantity * moved.Mrp),
                    WardIndentLineId = p.IndentLine?.Id,
                    QueueLineId = p.QueueLine?.Id,
                    PrescriptionLineId = p.QueueLine?.PrescriptionLineId,
                });
            }

            var recorded = p.IndentLine is not null ? indent!.RecordIssued(p.IndentLine.Id, p.Input.Quantity, now)
                : p.QueueLine is not null ? entry!.RecordDispensed(p.QueueLine.Id, p.Input.Quantity, now)
                : Result.Success();
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        issue.TotalAmount = issue.Lines.Sum(l => l.Amount);
        issue.MarkIssued();
        db.Add(issue);

        foreach (var line in issue.Lines)
        {
            var charge = await charges.PostAsync(new ChargeRequest
            {
                PatientId = patientId,
                VisitId = visitId,
                AdmissionId = admissionId,
                ServiceItemId = services.Value[ServiceCodeFor(line.ItemType)],
                Quantity = line.Quantity,
                UnitPriceOverride = line.UnitMrp,
                Description = $"{line.ItemName} (batch {line.BatchNumber})",
                SourceModule = ChargeModule,
                SourceReference = line.Id.ToString(),
                ServiceDate = now,
            }, ct);
            if (charge.IsFailure)
            {
                return PharmacyLookups.Prefixed(charge.Error!, $"Charging {line.ItemName} to the IP account failed");
            }

            line.ChargeId = charge.Value;
        }

        var prescriber = entry is null
            ? null
            : await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == entry.PractitionerId).Select(p => new { p.DisplayName, p.RegistrationNumber }).FirstOrDefaultAsync(ct);
        register.Record(issue.FacilityId, issued.Value.Movements, StockMovementType.Dispense, inward: false, reference,
            PharmacyLookups.Party(patient, null, prescriber?.DisplayName, prescriber?.RegistrationNumber, entry?.PrescriptionNumber), items, now);

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        if (indent is not null)
        {
            await realtime.WardIndentChangedAsync(indent.FacilityId, indent.Id, indent.Status, ct);
        }

        if (entry is not null)
        {
            await realtime.QueueChangedAsync(entry.FacilityId, entry.Id, entry.Status, ct);
        }

        return new IpIssueCreated(issue.Id, issue.Number, issue.TotalAmount);
    }

    // ------------------------------------------------------------------ ward returns

    public async Task<Result<WardReturnCreated>> ReturnAsync(Guid issueId, WardReturnRequest request, CancellationToken ct)
    {
        var issue = await db.Set<IpIssue>().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return Error.NotFound(nameof(IpIssue), issueId);
        }

        var access = await guard.EnsureCanOpenAsync(issue.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var services = await ServiceItemsAsync(ct);
        if (services.IsFailure)
        {
            return services.Error!;
        }

        var today = time.Today();
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var wardReturn = new WardReturn
        {
            FacilityId = issue.FacilityId,
            Number = await numbers.NextAsync(PharmacyNumberSeries.WardReturn, issue.FacilityId, ct),
            IpIssueId = issue.Id,
            PatientId = issue.PatientId,
            AdmissionId = issue.AdmissionId,
            ReturnedAt = now,
            Reason = request.Reason.Trim(),
        };
        var reference = new StockReference(PharmacyStockReferences.WardReturn, wardReturn.Id, wardReturn.Number, $"Ward return against {issue.Number}");
        var movements = new List<StockMovement>();
        foreach (var input in request.Lines)
        {
            var line = issue.Lines.FirstOrDefault(l => l.Id == input.IpIssueLineId);
            if (line is null)
            {
                return Error.NotFound(nameof(IpIssueLine), input.IpIssueLineId);
            }

            var wholeLine = line.ReturnedQuantity == 0 && input.Quantity == line.Quantity;
            var credit = issue.ReturnLine(line.Id, input.Quantity, now);
            if (credit.IsFailure)
            {
                return credit.Error!;
            }

            var restock = input.Restock && (line.ExpiryDate is not { } expiry || expiry >= today);
            if (restock)
            {
                var moved = await stock.ReturnToBatchAsync(line.BatchId, input.Quantity, StockMovementType.Return, reference, null, ct);
                if (moved.IsFailure)
                {
                    return moved.Error!;
                }

                movements.Add(moved.Value);
            }

            var returnLine = new WardReturnLine
            {
                WardReturnId = wardReturn.Id,
                IpIssueLineId = line.Id,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                BatchId = line.BatchId,
                BatchNumber = line.BatchNumber,
                Quantity = input.Quantity,
                CreditAmount = credit.Value,
                Restocked = restock,
                ChargeReversal = ChargeReversal.Credited,
            };

            // An untouched line is simply cancelled on the bill; if billing refuses (already billed) or only part
            // comes back, a negative-quantity credit line keeps the IP account right.
            if (wholeLine && (await charges.CancelAsync(ChargeModule, line.Id.ToString(), wardReturn.Reason, ct)).IsSuccess)
            {
                returnLine.ChargeReversal = ChargeReversal.Cancelled;
            }
            else
            {
                var credited = await charges.PostAsync(new ChargeRequest
                {
                    PatientId = issue.PatientId,
                    VisitId = issue.VisitId,
                    AdmissionId = issue.AdmissionId,
                    ServiceItemId = services.Value[ServiceCodeFor(line.ItemType)],
                    Quantity = -input.Quantity,
                    UnitPriceOverride = line.UnitMrp,
                    Description = $"Returned: {line.ItemName} (batch {line.BatchNumber})",
                    SourceModule = ChargeReturnModule,
                    SourceReference = returnLine.Id.ToString(),
                    ServiceDate = now,
                }, ct);
                if (credited.IsFailure)
                {
                    return PharmacyLookups.Prefixed(credited.Error!, $"Crediting {line.ItemName} to the IP account failed");
                }
            }

            wardReturn.Lines.Add(returnLine);
            wardReturn.CreditAmount += credit.Value;
        }

        db.Add(wardReturn);
        if (movements.Any(m => m.RequiresRegister))
        {
            var items = await InventoryQueries.ItemSummariesAsync(db, movements.Select(m => m.ItemId), ct);
            var patient = await PharmacyLookups.PatientAsync(db, issue.PatientId, ct);
            register.Record(issue.FacilityId, movements, StockMovementType.Return, inward: true, reference,
                PharmacyLookups.Party(patient, null, null, null, null), items, now);
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new WardReturnCreated(wardReturn.Id, wardReturn.Number, wardReturn.CreditAmount);
    }

    /// <summary>The catalogue services pharmacy lines are billed under (seeded by <c>PharmacySeeder</c>).</summary>
    private async Task<Result<Dictionary<string, Guid>>> ServiceItemsAsync(CancellationToken ct)
    {
        string[] codes = [MedicinesServiceCode, ConsumablesServiceCode, ImplantsServiceCode];
        var services = await db.Set<ServiceItem>().AsNoTracking()
            .Where(s => codes.Contains(s.Code) && s.IsActive)
            .ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        return services.Count == codes.Length
            ? services
            : Error.Rule("pharmacy.service_items_missing", "The pharmacy billing services (PHARM-MED, PHARM-CONS, PHARM-IMPL) are missing or inactive.");
    }
}
