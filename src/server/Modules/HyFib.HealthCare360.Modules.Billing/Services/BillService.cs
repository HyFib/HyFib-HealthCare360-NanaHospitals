using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// OP billing and the bill lifecycle (OPB-01..07, MB-05): draft from a visit's unbilled charges, ad-hoc services,
/// finalization under a gap-free number, cancellation with approval (charges released, payments made refundable) and
/// discarding drafts. IP-specific preparation lives in <see cref="IpBillingService"/>.
/// </summary>
public sealed class BillService(
    HospitalDbContext db,
    ChargeService charges,
    PayerCoverageService coverage,
    DiscountService discounts,
    RefundService refunds,
    AdvanceService advances,
    INumberSeries numbers,
    IRealtimePublisher realtime,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string OpSeries = "billing.bill.op";
    public const string OpFormat = "{FAC}OB{YY}{SEQ:7}";
    public const string IpSeries = "billing.bill.ip";
    public const string IpFormat = "{FAC}IB{YY}{SEQ:7}";

    public async Task<Result<Guid>> CreateOpDraftAsync(CreateBillRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired;
        }

        Visit? visit = null;
        Guid patientId;
        if (request.VisitId is { } visitId)
        {
            visit = await db.Set<Visit>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == visitId, ct);
            if (visit is null)
            {
                return Error.NotFound(nameof(Visit), visitId);
            }

            if (visit.Type == VisitType.InPatient)
            {
                return Error.Rule("billing.use_ip_account", "In-patient services are billed from the IP account.");
            }

            var draft = await db.Set<Bill>().Where(b => b.VisitId == visitId && b.Status == BillStatus.Draft).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
            if (draft is { } existing)
            {
                return Error.Conflict("billing.draft_exists", "This visit already has a draft bill.", new { billId = existing });
            }

            patientId = visit.PatientId;
        }
        else
        {
            patientId = request.PatientId!.Value;
        }

        var patient = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => new { p.MergedIntoPatientId, p.IsActive }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Bill the surviving record.");
        }

        var terms = visit is null ? await coverage.SelfPayAsync(facilityId, ct) : await coverage.ForVisitAsync(visit, ct);
        var bill = Bill.Draft(facilityId, BillType.OutPatient, false, patientId, visit?.Id, null, null, terms);
        db.Add(bill);
        await AttachAsync(bill, await AvailableOpChargesAsync(patientId, visit?.Id, request.ChargeIds, ct), ct);
        await db.SaveChangesAsync(ct);
        await PublishAsync(bill, ct);
        return bill.Id;
    }

    /// <summary>Adds a service directly on an OP draft (posted as a <c>billing.adhoc</c> charge, so it is priced and audited like any other).</summary>
    public async Task<Result> AddServiceAsync(Guid billId, AddBillLineRequest request, CancellationToken ct)
    {
        var bill = await BillQueries.LoadAsync(db, billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (bill.Status != BillStatus.Draft)
        {
            return Error.Rule("billing.bill_not_draft", "Only draft bills can be changed.");
        }

        if (bill.Type == BillType.InPatient)
        {
            return Error.Rule("billing.use_ip_charges", "Charge in-patient services on the IP account, then refresh the bill.");
        }

        db.GuardVersion(bill, request.Version);
        var item = await db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServiceItemId && s.IsActive, ct);
        if (item is null)
        {
            return Error.Field("serviceItemId", "Choose an active service.");
        }

        if (request.UnitPrice is not null && !item.IsPriceEditable)
        {
            return Error.Field("unitPrice", "The price of this service is fixed by the tariff.");
        }

        var staged = await charges.StageAsync(new ChargeRequest
        {
            PatientId = bill.PatientId,
            VisitId = bill.VisitId,
            ServiceItemId = item.Id,
            Quantity = request.Quantity,
            UnitPriceOverride = request.UnitPrice,
            Description = request.Description,
            SourceModule = ChargeService.AdhocSource,
            SourceReference = Guid.CreateVersion7().ToString("N"),
            PractitionerId = request.PractitionerId,
            DepartmentId = request.DepartmentId,
            ServiceDate = time.GetUtcNow(),
        }, isFreeFollowUp: false, ct);
        if (staged.IsFailure)
        {
            return staged.Error!;
        }

        await AttachAsync(bill, [staged.Value], ct);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RemoveLineAsync(Guid billId, Guid lineId, uint version, CancellationToken ct)
    {
        var bill = await BillQueries.LoadAsync(db, billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        db.GuardVersion(bill, version);
        var removed = bill.RemoveLine(lineId);
        if (removed.IsFailure)
        {
            return removed.Error!;
        }

        var charge = await db.Set<Charge>().FirstAsync(c => c.Id == removed.Value, ct);
        charge.Release();
        if (charge.SourceModule == ChargeService.AdhocSource)
        {
            charge.Cancel("Removed from the draft bill", time.GetUtcNow());
        }

        bill.Recalculate();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Pulls services posted since the OP draft was created (for example a lab test ordered meanwhile).</summary>
    public async Task<Result<int>> RefreshAsync(Guid billId, uint version, CancellationToken ct)
    {
        var bill = await BillQueries.LoadAsync(db, billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (bill.Status != BillStatus.Draft)
        {
            return Error.Rule("billing.bill_not_draft", "Only draft bills can be changed.");
        }

        if (bill.Type == BillType.InPatient)
        {
            return Error.Rule("billing.use_ip_refresh", "Refresh in-patient bills from the IP account.");
        }

        db.GuardVersion(bill, version);
        var available = await AvailableOpChargesAsync(bill.PatientId, bill.VisitId, null, ct);
        await AttachAsync(bill, available, ct);
        await db.SaveChangesAsync(ct);
        return available.Count;
    }

    /// <summary>
    /// Finalizes a draft: re-checks the discount against the finalizing user's authority, allocates the bill number in
    /// the same transaction as the update (gap-free), marks the charges billed and raises BillFinalized. An online
    /// prepayment of the visit's appointment is adjusted against the new OP bill in the same transaction.
    /// </summary>
    public async Task<Result> FinalizeAsync(Guid billId, uint version, CancellationToken ct)
    {
        var bill = await BillQueries.LoadAsync(db, billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (bill.Status != BillStatus.Draft)
        {
            return Error.Rule("billing.bill_not_draft", "This bill is already finalized.");
        }

        db.GuardVersion(bill, version);
        if (await db.Set<DiscountApproval>().AnyAsync(a => a.BillId == billId && a.Status == DiscountApprovalStatus.Pending, ct))
        {
            return Error.Rule("billing.discount_pending", "A discount on this bill is awaiting approval.");
        }

        var totals = bill.Recalculate();
        var authority = await discounts.AuthorityAsync(ct);
        if (DiscountPolicy.RequiresApproval(totals.UserDiscount, totals.DiscountBase, authority, bill.ApprovedDiscountAmount))
        {
            return Error.Rule("billing.discount_needs_approval", "The discount on this bill is above your limit. Request approval before finalizing.");
        }

        if (bill.IsFinalBill && bill.AdmissionId is { } admissionId
            && await db.Set<Charge>().AnyAsync(c => c.AdmissionId == admissionId && c.Status == ChargeStatus.Unbilled && c.BillId == null, ct))
        {
            return Error.Rule("billing.final_bill_stale", "New services were charged after this final bill was prepared. Refresh the final bill first.");
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var number = bill.Type == BillType.InPatient
            ? await numbers.NextAsync(IpSeries, bill.FacilityId, IpFormat, NumberResetPolicy.Yearly, ct)
            : await numbers.NextAsync(OpSeries, bill.FacilityId, OpFormat, NumberResetPolicy.Yearly, ct);
        var finalized = bill.Finalize(number, currentUser.UserId, time.GetUtcNow());
        if (finalized.IsFailure)
        {
            return finalized;
        }

        var chargeIds = bill.Lines.Select(l => l.ChargeId).ToList();
        foreach (var charge in await db.Set<Charge>().Where(c => chargeIds.Contains(c.Id)).ToListAsync(ct))
        {
            charge.MarkBilled();
        }

        foreach (var approval in await db.Set<DiscountApproval>().Where(a => a.BillId == billId && a.Status == DiscountApprovalStatus.Pending).ToListAsync(ct))
        {
            approval.Withdraw(time.GetUtcNow());
        }

        await advances.ApplyVisitPrepaymentAsync(bill, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishAsync(bill, ct);
        return Result.Success();
    }

    /// <summary>
    /// Cancels a finalized bill (holders of <c>billing.bills.cancel</c> only): charges return to the unbilled pool,
    /// advance adjustments go back to the IP deposit or the patient advance they came from, and any cash/card collected
    /// becomes a refund request that a second person must approve.
    /// </summary>
    public async Task<Result> CancelAsync(Guid billId, CancelBillRequest request, CancellationToken ct)
    {
        var bill = await BillQueries.LoadAsync(db, billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (await db.Set<InsuranceClaim>().AnyAsync(c => c.BillId == billId && c.Status != ClaimStatus.Rejected, ct))
        {
            return Error.Rule("billing.bill_has_claim", "An insurance claim is open on this bill. Settle or reject the claim first.");
        }

        db.GuardVersion(bill, request.Version);
        var reason = request.Reason.Trim();
        var now = time.GetUtcNow();
        var cancelled = bill.Cancel(reason, currentUser.UserId, now);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        foreach (var charge in await db.Set<Charge>().Where(c => c.BillId == bill.Id).ToListAsync(ct))
        {
            charge.Release();
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var advance = await AdvanceHeldByBillAsync(bill.Id, ct);
        if (advance > 0 && bill.IpAccountId is { } accountId)
        {
            var account = await db.Set<IpAccount>().FirstAsync(a => a.Id == accountId, ct);
            var restored = Math.Min(advance, bill.RefundableAmount);
            if (restored > 0 && bill.ApplyRefund(restored).IsSuccess)
            {
                account.ReverseAdjustment(restored);
                await refunds.StageAdvanceReturnAsync(bill, restored, $"Advance restored: bill {bill.BillNumber} cancelled", ct);
            }
        }
        else if (advance > 0)
        {
            await advances.RestoreForCancelledBillAsync(bill, ct);
        }

        var toRefund = bill.RefundableAmount - await refunds.OutstandingForBillAsync(bill.Id, ct);
        if (toRefund > 0)
        {
            await refunds.StageRequestAsync(bill.FacilityId, RefundPurpose.Bill, bill.PatientId, bill.Id, null, toRefund, $"Bill {bill.BillNumber} cancelled: {reason}", ct);
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.bill.cancelled", $"Cancelled bill {bill.BillNumber}: {reason}")
        {
            PatientId = bill.PatientId,
            EntityType = nameof(Bill),
            EntityId = bill.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { bill.NetAmount, bill.PaidAmount, refundRequested = Math.Max(toRefund, 0m) },
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishAsync(bill, ct);
        return Result.Success();
    }

    /// <summary>Deletes a draft: its charges return to the unbilled pool (ad-hoc services are cancelled).</summary>
    public async Task<Result> DiscardDraftAsync(Guid billId, uint version, CancellationToken ct)
    {
        var bill = await BillQueries.LoadAsync(db, billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (bill.Status != BillStatus.Draft)
        {
            return Error.Rule("billing.bill_not_draft", "Finalized bills are cancelled, not discarded.");
        }

        db.SetExpectedVersion(bill, version);
        await StageDiscardAsync(bill, "Draft bill discarded", ct);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Discards a draft in the caller's unit of work: its charges return to the unbilled pool (ad-hoc services, which
    /// exist only on the draft, are cancelled) and pending discount requests are withdrawn. Does not save.
    /// </summary>
    internal async Task StageDiscardAsync(Bill bill, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var reserved = await db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(c => c.BillId == bill.Id).ToListAsync(ct);
        reserved.AddRange(db.Set<Charge>().Local.Where(c => c.BillId == bill.Id && !reserved.Contains(c)));
        foreach (var charge in reserved)
        {
            charge.Release();
            if (charge.SourceModule == ChargeService.AdhocSource)
            {
                charge.Cancel(reason, now);
            }
        }

        foreach (var approval in await db.Set<DiscountApproval>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                     .Where(a => a.BillId == bill.Id && a.Status == DiscountApprovalStatus.Pending).ToListAsync(ct))
        {
            approval.Withdraw(now);
        }

        bill.Lines.Clear();
        db.Remove(bill);
    }

    /// <summary>Adds charges to a draft (marking payer-excluded items) and recalculates it. Does not save.</summary>
    internal async Task AttachAsync(Bill bill, IReadOnlyCollection<Charge> available, CancellationToken ct)
    {
        if (available.Count > 0)
        {
            var isNonPayable = await coverage.NonPayableMatcherAsync(bill.HasPayer ? bill.PayerId : null, ct);
            var itemIds = available.Select(c => c.ServiceItemId).Distinct().ToList();
            var sacCodes = await db.Set<ServiceItem>().Where(s => itemIds.Contains(s.Id)).Select(s => new { s.Id, s.SacCode }).ToDictionaryAsync(s => s.Id, s => s.SacCode, ct);
            foreach (var charge in available.OrderBy(c => c.ServiceDate))
            {
                bill.AddCharge(charge, !isNonPayable(charge.ServiceItemId, charge.Category), sacCodes.GetValueOrDefault(charge.ServiceItemId));
            }
        }

        bill.Recalculate();
    }

    private async Task<List<Charge>> AvailableOpChargesAsync(Guid patientId, Guid? visitId, IReadOnlyList<Guid>? chargeIds, CancellationToken ct)
    {
        var query = db.Set<Charge>().Where(c => c.PatientId == patientId && c.Status == ChargeStatus.Unbilled && c.BillId == null && c.AdmissionId == null);
        query = visitId is { } id ? query.Where(c => c.VisitId == id) : query.Where(c => c.VisitId == null);
        if (chargeIds is { Count: > 0 })
        {
            query = query.Where(c => chargeIds.Contains(c.Id));
        }

        return await query.OrderBy(c => c.ServiceDate).ToListAsync(ct);
    }

    /// <summary>Advance (deposit) adjusted on this bill and not yet restored.</summary>
    private async Task<decimal> AdvanceHeldByBillAsync(Guid billId, CancellationToken ct)
    {
        var adjusted = await db.Set<Receipt>().Where(r => r.BillId == billId).SelectMany(r => r.Payments)
            .Where(p => p.Mode == PaymentMode.Advance).SumAsync(p => p.Amount, ct);
        var restored = await db.Set<Refund>()
            .Where(r => r.BillId == billId && r.Mode == PaymentMode.Advance && r.Status == RefundStatus.Paid).SumAsync(r => r.Amount, ct);
        return adjusted - restored;
    }

    private Task PublishAsync(Bill bill, CancellationToken ct) =>
        realtime.ToFacilityAsync(bill.FacilityId, "billing.bill.changed", new { bill.Id, bill.PatientId, bill.Status }, ct);
}
