using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// IP pharmacy (PH-03): nurses raise ward indents per patient; the IP pharmacist issues against the indent, the
/// in-patient prescription or directly. Each issued line is charged to the admission through
/// <see cref="IChargePoster"/> at MRP (SourceModule "pharmacy.ip", SourceReference = issue line id, so retries
/// never double-charge), feeds the MAR through <see cref="MedicationDispensed"/>, and returns from the ward
/// restock the batch and reverse the charge (cancel the untouched line, otherwise a negative credit line). A return with
/// a line not restocked is a write-off, made only when a second pharmacist approves it (<see cref="WardReturnKind"/>).
/// </summary>
public sealed class InpatientPharmacyService(
    HospitalDbContext db,
    INumberSeries numbers,
    StockService stock,
    IChargePoster charges,
    ControlledDrugRegisterService register,
    PharmacyRealtime realtime,
    PatientAccessGuard guard,
    IAntimicrobialStewardship stewardship,
    IApprovals approvals,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>A ward return with a line not restocked (a write-off): one pharmacist asks, another approves.</summary>
    public const string WardReturnKind = "pharmacy.ward_return";

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

    /// <summary>
    /// What a unit is charged: its MRP, or a price-controlled implant's NPPA ceiling price when that is lower, as for a pack
    /// printed before the ceiling was cut (DPCO 2013).
    /// </summary>
    internal static decimal PriceOf(ItemSummary item, decimal mrp) =>
        item.Type == ItemType.Implant && item.CeilingPrice is { } ceiling && ceiling > 0 && ceiling < mrp ? ceiling : mrp;

    /// <summary>What the bill says of a device issued (NPPA): its brand, maker, specification, batch and MRP; none for a medicine.</summary>
    internal static DeviceDetails? DeviceOf(ItemSummary item, IpIssueLine line) => item.Type == ItemType.Drug
        ? null
        : DeviceDetails.Of(item.BrandName, item.Manufacturer, item.Specification, line.BatchNumber, null, line.UnitMrp,
            item.Type == ItemType.Implant ? item.CeilingPrice : null);

    /// <summary>
    /// A device returned, on its credit line: as it was issued. Its ceiling is the one it was charged at, when that was below
    /// the pack's MRP; a ceiling revised since is not what was charged.
    /// </summary>
    internal static DeviceDetails? ReturnedDeviceOf(ItemSummary item, IpIssueLine line) =>
        DeviceOf(item, line) is { } device ? device with { CeilingPrice = line.UnitPrice < line.UnitMrp ? line.UnitPrice : null } : null;

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

        // A restricted antibiotic goes out for its first doses only while it waits for stewardship approval.
        var prescriptionLines = planned.Where(p => p.QueueLine is not null).Select(p => p.QueueLine!.PrescriptionLineId).ToList();
        if (await stewardship.IssueRefusalAsync(prescriptionLines, ct) is { } refusal)
        {
            return Error.Rule("ams.approval_required", refusal);
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
                var unitPrice = PriceOf(p.Item, moved.Mrp);
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
                    UnitPrice = unitPrice,
                    Amount = Money.Round(moved.Quantity * unitPrice),
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
            // A device's brand, maker, batch and MRP print beneath its line (NPPA); a medicine's batch stays in its name.
            var device = DeviceOf(items[line.ItemId], line);
            var charge = await charges.PostAsync(new ChargeRequest
            {
                PatientId = patientId,
                VisitId = visitId,
                AdmissionId = admissionId,
                ServiceItemId = services.Value[ServiceCodeFor(line.ItemType)],
                Quantity = line.Quantity,
                UnitPriceOverride = line.UnitPrice,
                Description = device is null ? $"{line.ItemName} (batch {line.BatchNumber})" : line.ItemName,
                SourceModule = ChargeModule,
                SourceReference = line.Id.ToString(),
                ServiceDate = now,
                Device = device,
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

    /// <summary>A ward return of the issue waiting for a second pharmacist, for every reader of the issue.</summary>
    public Task<IReadOnlyList<PendingApproval>> PendingReturnsAsync(Guid issueId, CancellationToken ct) =>
        PendingApprovals.ForAsync(db, nameof(IpIssue), issueId, [WardReturnKind], currentUser.UserId, time.GetUtcNow(), ct);

    /// <summary>
    /// A ward return. Restocked, it is made at once, as before; a line not restocked (opened, damaged or expired) is a
    /// write-off, and the return waits for a second pharmacist (<see cref="WardReturnApprovedAsync"/>): nothing is credited
    /// or written off until then.
    /// </summary>
    public async Task<Result<WardReturnOutcome>> ReturnAsync(Guid issueId, WardReturnRequest request, CancellationToken ct)
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

        var plan = issue.PlanReturn(request.Lines.Select(l => new ReturnInput(l.IpIssueLineId, l.Quantity, l.Restock)).ToList(), time.Today());
        if (plan.IsFailure)
        {
            return plan.Error!;
        }

        var reason = request.Reason.Trim();
        if (plan.Value.WritesOff)
        {
            var asked = await AskWardReturnAsync(issue, plan.Value, reason, ct);
            return asked.IsFailure ? asked.Error! : new WardReturnOutcome(null, asked.Value);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var made = await MakeWardReturnAsync(issue, plan.Value, reason, services.Value, null, ct);
        if (made.IsFailure)
        {
            return made.Error!;
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new WardReturnOutcome(made.Value, null);
    }

    /// <summary>
    /// Makes a ward return a second pharmacist approved, in the approval's transaction (the approval saves): only over the
    /// issue as it was asked for, and only if it would still be credited and written off exactly as approved.
    /// </summary>
    internal async Task<Result<string>> WardReturnApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<WardReturnPayload>(approval.Payload);
        var issue = await db.Set<IpIssue>().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == asked.IpIssueId, ct);
        if (issue is null)
        {
            return Error.NotFound(nameof(IpIssue), asked.IpIssueId);
        }

        // The approving pharmacist opens the patient's record to decide: one who may not is refused, and the request waits.
        var access = await guard.EnsureCanOpenAsync(issue.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        if (issue.Version != approval.SubjectVersion)
        {
            return IssueChanged(issue);
        }

        var services = await ServiceItemsAsync(ct);
        if (services.IsFailure)
        {
            return services.Error!;
        }

        var plan = issue.PlanReturn(asked.Lines, time.Today());
        if (plan.IsFailure)
        {
            return Error.Rule("approval.stale", $"The return from the ward of issue {issue.Number} can no longer be made, so nothing was changed: {plan.Error!.Message}");
        }

        if (plan.Value.Credit != asked.Credit || !plan.Value.Lines.Select(l => new ReturnInput(l.Line.Id, l.Quantity, l.Restock)).SequenceEqual(asked.Lines))
        {
            return IssueChanged(issue);
        }

        var made = await MakeWardReturnAsync(issue, plan.Value, asked.Reason, services.Value, approval, ct);
        if (made.IsFailure)
        {
            return made.Error!;
        }

        var writtenOff = plan.Value.Lines.Where(l => !l.Restock).Select(l => $"{l.Line.ItemName} × {StockErrors.Format(l.Quantity)}");
        var outcome = $"Ward return {made.Value.Number}: ₹{Money.FormatInr(made.Value.CreditAmount)} credited to the IP account; written off: {string.Join(", ", writtenOff)}";
        audit.Add(new AuditEntry(AuditCategories.Data, "pharmacy.ward_return_written_off",
            Fit($"{outcome}. Asked for by {approval.RequestedByName}, approved by {CheckerName()}: {asked.Reason}", 1000))
        {
            PatientId = issue.PatientId,
            EntityType = nameof(IpIssue),
            EntityId = issue.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { issue.Number, wardReturn = made.Value.Number, approval = approval.Id, requestedBy = approval.RequestedBy, approvedBy = currentUser.UserId },
        });
        return outcome;
    }

    /// <summary>
    /// Asks a second pharmacist to approve a ward return with lines not restocked, telling them what is written off, whether a
    /// controlled or high-alert drug is among them, and what the patient is credited.
    /// </summary>
    private async Task<Result<ApprovalRequestDto>> AskWardReturnAsync(IpIssue issue, WardReturnPlan plan, string reason, CancellationToken ct)
    {
        var patient = await PharmacyLookups.PatientAsync(db, issue.PatientId, ct);
        var itemIds = plan.Lines.Select(l => l.Line.ItemId).ToList();
        var highAlert = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.IsHighAlert).Select(i => i.Id).ToListAsync(ct);
        var flagged = plan.Lines.Where(l => !l.Restock && (Item.NeedsRegister(l.Line.Schedule, l.Line.IsControlled) || highAlert.Contains(l.Line.ItemId))).ToList();
        var party = patient is null ? "Unknown patient" : $"{patient.Name} ({patient.Uhid})";
        List<ApprovalDetail> details =
        [
            new("Issue", $"{issue.Number}, issued {DocumentFormat.DateTime(issue.IssuedAt)}"),
            new("Patient", party),
            new("Items returned", string.Join("; ", plan.Lines.Select(l =>
                $"{l.Line.ItemName} × {StockErrors.Format(l.Quantity)} (batch {l.Line.BatchNumber}), {(l.Restock ? "back on the shelf" : l.Expired ? "not restocked (expired): written off" : "not restocked: written off")}"))),
            new("Credited to the IP account", DocumentFormat.Money(plan.Credit)),
        ];
        if (flagged.Count > 0)
        {
            details.Add(new("Controlled or high-alert", string.Join("; ", flagged.Select(l =>
                Item.NeedsRegister(l.Line.Schedule, l.Line.IsControlled)
                    ? $"{l.Line.ItemName}: entered in the controlled-drug register as returned from the ward and written off, naming you"
                    : $"{l.Line.ItemName} (high-alert): written off in the stock ledger, naming you"))));
        }

        var notRestocked = plan.Lines.Count(l => !l.Restock);
        var summary = $"Ward return on issue {issue.Number} ({party}): {notRestocked} line{(notRestocked == 1 ? string.Empty : "s")} not restocked (written off), ₹{Money.FormatInr(plan.Credit)} credited";
        var payload = new WardReturnPayload(issue.Id, plan.Lines.Select(l => new ReturnInput(l.Line.Id, l.Quantity, l.Restock)).ToList(), reason, plan.Credit);
        return await approvals.RequestAsync(WardReturnKind, new ApprovalSubject(nameof(IpIssue), issue.Id, issue.Version, issue.FacilityId, issue.PatientId), payload,
            Fit(summary, 300), details, reason, ct);
    }

    /// <summary>
    /// Makes the ward return worked out in <paramref name="plan"/>, in the caller's transaction: restocked lines go back into
    /// their batch; a line not restocked comes back and goes out again as a write-off, so the ledger and, for a controlled
    /// drug, the register show what came back from the ward and what was destroyed; and the patient is credited.
    /// </summary>
    private async Task<Result<WardReturnCreated>> MakeWardReturnAsync(
        IpIssue issue, WardReturnPlan plan, string reason, Dictionary<string, Guid> services, ApprovalRequest? approval, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var wardReturn = new WardReturn
        {
            FacilityId = issue.FacilityId,
            Number = await numbers.NextAsync(PharmacyNumberSeries.WardReturn, issue.FacilityId, ct),
            IpIssueId = issue.Id,
            PatientId = issue.PatientId,
            AdmissionId = issue.AdmissionId,
            ReturnedAt = now,
            Reason = reason,
        };
        var approved = approval is null ? string.Empty : $"; asked for by {approval.RequestedByName}, approved by {CheckerName()}";
        var reference = new StockReference(PharmacyStockReferences.WardReturn, wardReturn.Id, wardReturn.Number, Fit($"Ward return against {issue.Number}{approved}", 300));
        var writeOff = reference with { Remarks = Fit($"Returned from the ward against {issue.Number}, not restocked: {reason}{approved}", 300) };
        var restocked = new List<StockMovement>();
        var cameBack = new List<StockMovement>();
        var writtenOff = new List<(StockMovement Movement, StockMovementType Type)>();
        var returnedItems = await InventoryQueries.ItemSummariesAsync(db, issue.Lines.Select(l => l.ItemId), ct);
        foreach (var planned in plan.Lines)
        {
            var line = planned.Line;
            var wholeLine = line.ReturnedQuantity == 0 && planned.Quantity == line.Quantity;
            var credit = issue.ReturnLine(line.Id, planned.Quantity, now);
            if (credit.IsFailure)
            {
                return credit.Error!;
            }

            var moved = await stock.ReturnToBatchAsync(line.BatchId, planned.Quantity, StockMovementType.Return, planned.Restock ? reference : writeOff, null, ct);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            if (planned.Restock)
            {
                restocked.Add(moved.Value);
            }
            else
            {
                cameBack.Add(moved.Value);
                var type = planned.Expired ? StockMovementType.ExpiryWriteOff : StockMovementType.Adjustment;
                var destroyed = await stock.AdjustBatchAsync(line.BatchId, -planned.Quantity, type, writeOff, ct);
                if (destroyed.IsFailure)
                {
                    return destroyed.Error!;
                }

                writtenOff.Add((destroyed.Value, type));
            }

            var returnLine = new WardReturnLine
            {
                WardReturnId = wardReturn.Id,
                IpIssueLineId = line.Id,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                BatchId = line.BatchId,
                BatchNumber = line.BatchNumber,
                Quantity = planned.Quantity,
                CreditAmount = credit.Value,
                Restocked = planned.Restock,
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
                // A device's credit line carries its details, as its charge did; a medicine's names its batch.
                var device = returnedItems.TryGetValue(line.ItemId, out var item) ? ReturnedDeviceOf(item, line) : null;
                var credited = await charges.PostAsync(new ChargeRequest
                {
                    PatientId = issue.PatientId,
                    VisitId = issue.VisitId,
                    AdmissionId = issue.AdmissionId,
                    ServiceItemId = services[ServiceCodeFor(line.ItemType)],
                    Quantity = -planned.Quantity,
                    UnitPriceOverride = line.UnitPrice,
                    Description = device is null ? $"Returned: {line.ItemName} (batch {line.BatchNumber})" : $"Returned: {line.ItemName}",
                    SourceModule = ChargeReturnModule,
                    SourceReference = returnLine.Id.ToString(),
                    ServiceDate = now,
                    Device = device,
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
        if (restocked.Concat(cameBack).Concat(writtenOff.Select(w => w.Movement)).Any(m => m.RequiresRegister))
        {
            var items = await InventoryQueries.ItemSummariesAsync(db, issue.Lines.Select(l => l.ItemId), ct);
            var party = PharmacyLookups.Party(await PharmacyLookups.PatientAsync(db, issue.PatientId, ct), null, null, null, null);
            register.Record(issue.FacilityId, restocked, StockMovementType.Return, inward: true, reference, party, items, now);
            register.Record(issue.FacilityId, cameBack, StockMovementType.Return, inward: true, writeOff, party, items, now);
            foreach (var group in writtenOff.GroupBy(w => w.Type))
            {
                register.Record(issue.FacilityId, group.Select(w => w.Movement), group.Key, inward: false, writeOff, party, items, now);
            }
        }

        return new WardReturnCreated(wardReturn.Id, wardReturn.Number, wardReturn.CreditAmount);
    }

    private static Error IssueChanged(IpIssue issue) => Error.Rule("approval.stale",
        $"Issue {issue.Number} changed after this was asked for (another return was taken), so nothing was changed. Ask again, against the issue as it is now.");

    private string CheckerName() => currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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

/// <summary>The ward return asked for, exactly as it will be made: its lines (each restocked or written off), why, and the credit.</summary>
public sealed record WardReturnPayload(Guid IpIssueId, IReadOnlyList<ReturnInput> Lines, string Reason, decimal Credit);

/// <summary>A ward return made at once (<see cref="Created"/>: everything restocked), or asked for (<see cref="Asked"/>).</summary>
public sealed record WardReturnOutcome(WardReturnCreated? Created, ApprovalRequestDto? Asked);

/// <summary>
/// A ward return with a line not restocked: a write-off, and for a controlled drug an entry in the register. One
/// pharmacist asks and another who issues to the wards approves (the framework refuses the one who asked), as a second
/// pharmacist witnesses what is destroyed.
/// </summary>
internal sealed class WardReturnApproval(InpatientPharmacyService ip) : IApprovalAction
{
    public string Kind => InpatientPharmacyService.WardReturnKind;

    public string Title => "Ward return not restocked";

    public string ApprovePermission => PharmacyPermissions.IpIssue;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => ip.WardReturnApprovedAsync(request, ct);
}
