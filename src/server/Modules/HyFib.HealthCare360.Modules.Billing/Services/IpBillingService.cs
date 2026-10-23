using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// In-patient billing (IPB-01..08). The IP account opens on PatientAdmitted, follows bed transfers (room-category
/// pricing), drafts the final bill on DischargeInitiated (package vs actuals, insurance/patient split) and records the
/// discharge. Staff use it for the running bill, interim bills, packages, final settlement with the advance and refund of
/// any excess, and the discharge clearance the ward checks before the patient leaves.
/// </summary>
public sealed class IpBillingService(
    HospitalDbContext db,
    ChargeService charges,
    BillService bills,
    PayerCoverageService coverage,
    ReceiptService receipts,
    RefundService refunds,
    TariffService tariffs,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<IpBillingService> logger)
{
    // ------------------------------------------------------------------ admission events
    // These run inside the Inpatient module's SaveChanges (same transaction): they stage changes and never save.

    internal async Task OpenAccountAsync(PatientAdmitted admitted, CancellationToken ct)
    {
        if (await charges.FindAccountAsync(admitted.AdmissionId, ct) is not null)
        {
            return;
        }

        var visit = await db.Set<Visit>().FindAsync([admitted.VisitId], ct);
        var payerId = visit?.PayerId;
        var plan = await tariffs.ResolvePlanAsync(admitted.PayerType, payerId, admitted.FacilityId, ct);
        Guid? policyId = null;
        if (payerId is { } id && admitted.PayerType is PayerType.Insurance or PayerType.Corporate or PayerType.GovernmentScheme)
        {
            var onDate = BillingTime.LocalDate(admitted.AdmittedAt);
            policyId = await db.Set<PatientPolicy>()
                .Where(p => p.PatientId == admitted.PatientId && p.PayerId == id && p.IsActive && p.ValidFrom <= onDate && p.ValidTo >= onDate)
                .OrderByDescending(p => p.ValidTo)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(ct);
        }

        var account = IpAccount.Open(admitted, payerId, plan?.Id, policyId);
        db.Add(account);

        // Charges posted for this admission earlier in the same transaction were priced before the account existed.
        var staged = db.ChangeTracker.Entries<Charge>()
            .Where(e => e.State == EntityState.Added && e.Entity.AdmissionId == admitted.AdmissionId)
            .Select(e => e.Entity)
            .ToList();
        foreach (var charge in staged)
        {
            charge.FacilityId = account.FacilityId;
        }

        await charges.RepriceAsync(staged, plan, account.BedServiceItemId, ct);
    }

    internal async Task MoveBedAsync(PatientTransferred transferred, CancellationToken ct)
    {
        var account = await charges.FindAccountAsync(transferred.AdmissionId, ct);
        account?.MoveToBed(transferred.ToBedId, transferred.ToBedServiceItemId);
    }

    internal async Task DischargeInitiatedAsync(DischargeInitiated initiated, CancellationToken ct)
    {
        var account = await charges.FindAccountAsync(initiated.AdmissionId, ct);
        if (account is null)
        {
            return;
        }

        account.MarkDischargeInitiated(initiated.InitiatedAt);
        if (await HasFinalizedFinalBillAsync(account.AdmissionId, ct))
        {
            return;
        }

        var bill = await StageFinalBillAsync(account, ct);
        if (bill.Lines.Count == 0)
        {
            // Nothing is left for a final bill: the interim bills of a stay billed in parts took every service, or none
            // was charged. An empty bill cannot be finalized, so none is drafted; the advance is settled against the
            // interim bills.
            Discard(bill);
            var charged = await db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .AnyAsync(c => c.AdmissionId == account.AdmissionId && c.Status != ChargeStatus.Cancelled, ct);
            var next = await HasDraftInterimBillAsync(account.AdmissionId, ct)
                ? "Finalize the draft interim bill, then settle the account."
                : account.DepositBalance > 0 ? $"Settle the advance of ₹{Money.FormatInr(account.DepositBalance)}." : "No advance is held.";
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.IpManage,
                account.FacilityId,
                "Discharge initiated — nothing left to bill",
                $"Admission {account.AdmissionNumber}: {(charged ? "every service is on an interim bill" : "no service was charged")}, so there is no final bill. {next}",
                $"/billing/ip-accounts/{account.Id}",
                "billing",
                "info",
                ct);
            return;
        }

        await notifications.NotifyPermissionHoldersAsync(
            BillingPermissions.IpManage,
            account.FacilityId,
            "Discharge initiated — final bill drafted",
            $"Admission {account.AdmissionNumber}: the draft final bill (₹{Money.FormatInr(bill.NetAmount)}) is ready for review.",
            $"/billing/ip-accounts/{account.Id}",
            "billing",
            "info",
            ct);
    }

    /// <summary>
    /// The discharge was revoked: the account is open again, so the stay is not settled while the patient is in a bed, and
    /// the billing desk is told that a final bill drafted for the discharge waits for the new one.
    /// </summary>
    internal async Task DischargeRevokedAsync(DischargeRevoked revoked, CancellationToken ct)
    {
        var account = await charges.FindAccountAsync(revoked.AdmissionId, ct);
        if (account is not { Status: IpAccountStatus.DischargeInitiated })
        {
            return;
        }

        account.ReopenAfterRevokedDischarge();
        await notifications.NotifyPermissionHoldersAsync(
            BillingPermissions.IpManage,
            account.FacilityId,
            "Discharge revoked — the stay goes on",
            $"Admission {account.AdmissionNumber}: the discharge was revoked. Do not settle the account yet; a final bill already drafted stays a draft until the next discharge.",
            $"/billing/ip-accounts/{account.Id}",
            "billing",
            "info",
            ct);
    }

    internal async Task DischargedAsync(PatientDischarged discharged, CancellationToken ct)
    {
        var account = await charges.FindAccountAsync(discharged.AdmissionId, ct);
        if (account is null)
        {
            return;
        }

        account.MarkDischarged(discharged.DischargedAt, discharged.DischargeType);
        var position = await BillingStatusProvider.PositionAsync(db, discharged.AdmissionId, ct);
        var clearance = DischargeClearanceRules.Evaluate(position);

        // Every service is on a finalized bill (the final bill, or the interim bills that took them all), nothing is due
        // and no advance is held.
        if (clearance.Outstanding <= 0 && position.UnbilledCount == 0 && account.DepositBalance <= 0)
        {
            account.Close();
            return;
        }

        if (clearance.Outstanding > 0)
        {
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.IpManage,
                account.FacilityId,
                "Patient discharged with dues",
                $"Admission {account.AdmissionNumber} was discharged with ₹{Money.FormatInr(clearance.Outstanding)} outstanding. {clearance.Reason}",
                $"/billing/ip-accounts/{account.Id}",
                "billing",
                "warning",
                ct);
        }
    }

    // ------------------------------------------------------------------ queries

    public async Task<Result<IpAccountDetail>> DetailAsync(Guid id, CancellationToken ct)
    {
        var a = await db.Set<IpAccount>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        var patient = (await PatientQueries.SummariesAsync(db, [a.PatientId], BillingTime.Today(time), ct)).GetValueOrDefault(a.PatientId);
        var payerName = a.PayerId is { } payerId ? await db.Set<Payer>().Where(p => p.Id == payerId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var planName = a.TariffPlanId is { } planId ? await db.Set<TariffPlan>().Where(p => p.Id == planId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var doctor = a.AttendingPractitionerId is { } doctorId
            ? await db.Set<Practitioner>().Where(p => p.Id == doctorId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        var bedCode = a.BedServiceItemId is { } bedId ? await db.Set<ServiceItem>().Where(s => s.Id == bedId).Select(s => s.Code).FirstOrDefaultAsync(ct) : null;
        var packageName = a.PackageId is { } packageId ? await db.Set<Package>().Where(p => p.Id == packageId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;

        return new IpAccountDetail(
            a.Id, a.AdmissionId, a.AdmissionNumber, a.PatientId, patient?.Uhid, patient?.FullName, a.VisitId, a.PayerType, a.PayerId, payerName,
            a.TariffPlanId, planName, a.PolicyId, a.AttendingPractitionerId, doctor, a.BedServiceItemId, bedCode, a.AdmittedAt, a.DischargeInitiatedAt,
            a.DischargedAt, a.Status, a.EstimatedAmount, a.DepositReceived, a.DepositAdjusted, a.DepositRefunded, a.DepositBalance, a.PackageId,
            packageName, a.DischargeWithDuesApproved, a.DischargeApprovalReason, a.Notes, a.Version);
    }

    public async Task<Result<RunningBill>> RunningBillAsync(Guid id, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        var all = await db.Set<Charge>().AsNoTracking()
            .Where(c => c.AdmissionId == account.AdmissionId && c.Status != ChargeStatus.Cancelled)
            .OrderBy(c => c.ServiceDate)
            .ToListAsync(ct);
        var practitionerIds = all.Where(c => c.PractitionerId is not null).Select(c => c.PractitionerId!.Value).Distinct().ToList();
        var doctors = await db.Set<Practitioner>().Where(p => practitionerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var paidOnBills = await db.Set<Bill>().AsNoTracking()
            .Where(b => b.AdmissionId == account.AdmissionId && b.Status != BillStatus.Draft && b.Status != BillStatus.Cancelled && b.Status != BillStatus.Refunded)
            .SumAsync(b => b.PaidAmount - b.RefundedAmount, ct);

        var services = all.Where(c => c.SourceModule != ChargeService.PackageSource).ToList();
        var package = await EvaluatePackageAsync(account, services, ct);
        var terms = await coverage.ForAccountAsync(account, ct);
        var estimate = await EstimateAsync(services, package, terms, ct);
        var payerName = terms.HasPayer ? await db.Set<Payer>().Where(p => p.Id == terms.PayerId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;

        return new RunningBill(
            account.Id,
            account.AdmissionNumber,
            account.Status,
            all.GroupBy(c => c.Category).OrderBy(g => g.Key)
                .Select(g => new RunningBillCategory(g.Key, g.Count(), g.Sum(c => c.GrossAmount), g.Sum(c => c.DiscountAmount), g.Sum(c => c.TaxAmount), g.Sum(c => c.NetAmount)))
                .ToList(),
            all.GroupBy(c => BillingTime.LocalDate(c.ServiceDate)).OrderBy(g => g.Key)
                .Select(g => new RunningBillDay(g.Key, g.Count(), g.Sum(c => c.NetAmount)))
                .ToList(),
            all.Select(c => new RunningBillLine(
                c.Id, c.ServiceDate, c.ServiceCode, c.Description, c.Category, c.Quantity, c.UnitPrice, c.NetAmount, c.Status, c.BillId,
                c.PractitionerId is { } pid ? doctors.GetValueOrDefault(pid) : null, c.IsCredit)).ToList(),
            GrossTotal: all.Sum(c => c.GrossAmount),
            DiscountTotal: all.Sum(c => c.DiscountAmount),
            TaxTotal: all.Sum(c => c.TaxAmount),
            NetTotal: all.Sum(c => c.NetAmount),
            UnbilledNet: all.Where(c => c.Status == ChargeStatus.Unbilled).Sum(c => c.NetAmount),
            BilledNet: all.Where(c => c.Status == ChargeStatus.Billed).Sum(c => c.NetAmount),
            EstimatedPatientPayable: estimate.PatientAmount,
            PaidOnBills: paidOnBills,
            DepositBalance: account.DepositBalance,
            EstimatedBalance: estimate.PatientAmount - paidOnBills - account.DepositBalance,
            EstimatedAmount: account.EstimatedAmount,
            Package: package?.Status,
            Insurance: terms.HasPayer
                ? new InsuranceEstimate(terms.PayerId, payerName, terms.CoverageLimit, terms.CoPayPercent, estimate.PayerAmount, estimate.PatientAmount)
                : null,
            ReturnsNet: all.Where(c => c.IsCredit).Sum(c => c.NetAmount));
    }

    public async Task<Result<PackageStatus?>> PackageStatusAsync(Guid id, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        var services = await db.Set<Charge>().AsNoTracking()
            .Where(c => c.AdmissionId == account.AdmissionId && c.Status != ChargeStatus.Cancelled && c.SourceModule != ChargeService.PackageSource)
            .ToListAsync(ct);
        return Result<PackageStatus?>.Ok((await EvaluatePackageAsync(account, services, ct))?.Status);
    }

    public async Task<Result<ClearanceDto>> ClearanceAsync(Guid id, CancellationToken ct)
    {
        var admissionId = await db.Set<IpAccount>().Where(a => a.Id == id).Select(a => (Guid?)a.AdmissionId).FirstOrDefaultAsync(ct);
        if (admissionId is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        var position = await BillingStatusProvider.PositionAsync(db, admissionId.Value, ct);
        var clearance = DischargeClearanceRules.Evaluate(position);
        return new ClearanceDto(
            clearance.Cleared, clearance.Outstanding, clearance.Reason, position.UnbilledCount, position.UnbilledAmount, position.HasFinalBill,
            position.PatientDue, position.DepositBalance, position.DischargeWithDuesApproved, position.BilledOnInterimBills);
    }

    // ------------------------------------------------------------------ commands

    public async Task<Result> UpdateAsync(Guid id, UpdateIpAccountRequest request, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        if (!account.IsActive)
        {
            return Error.Rule("billing.ip_account_inactive", "The patient has been discharged; the account's payer can no longer change.");
        }

        db.SetExpectedVersion(account, request.Version);
        if (request.PayerId is { } payerId && !await db.Set<Payer>().AnyAsync(p => p.Id == payerId && p.IsActive, ct))
        {
            return Error.Field("payerId", "Choose an active payer.");
        }

        if (request.TariffPlanId is { } planId && !await db.Set<TariffPlan>().AnyAsync(p => p.Id == planId && p.IsActive, ct))
        {
            return Error.Field("tariffPlanId", "Choose an active tariff plan.");
        }

        if (request.PolicyId is { } policyId && !await db.Set<PatientPolicy>().AnyAsync(p => p.Id == policyId && p.PatientId == account.PatientId, ct))
        {
            return Error.Field("policyId", "Choose one of this patient's policies.");
        }

        if (request.RepriceUnbilledCharges && await db.Set<Bill>().AnyAsync(b => b.AdmissionId == account.AdmissionId && b.Status == BillStatus.Draft, ct))
        {
            return Error.Rule("billing.draft_bill_exists", "Discard the admission's draft bill before re-pricing its services.");
        }

        account.EstimatedAmount = request.EstimatedAmount;
        account.PayerType = request.PayerType;
        account.PayerId = request.PayerId;
        account.PolicyId = request.PolicyId;
        account.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        account.TariffPlanId = request.TariffPlanId ?? (await tariffs.ResolvePlanAsync(request.PayerType, request.PayerId, account.FacilityId, ct))?.Id;

        if (request.RepriceUnbilledCharges)
        {
            var plan = await tariffs.FindPlanAsync(account.TariffPlanId, ct);
            var unbilled = await db.Set<Charge>()
                .Where(c => c.AdmissionId == account.AdmissionId && c.Status == ChargeStatus.Unbilled && c.BillId == null)
                .ToListAsync(ct);
            await charges.RepriceAsync(unbilled, plan, null, ct);
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Assigns (or removes) the admission's package; the old package's charge is cancelled and a draft final bill re-prepared.</summary>
    public async Task<Result> AssignPackageAsync(Guid id, AssignPackageRequest request, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        db.SetExpectedVersion(account, request.Version);
        if (request.PackageId is { } packageId)
        {
            var package = await db.Set<Package>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId && p.IsActive, ct);
            if (package is null)
            {
                return Error.Field("packageId", "Choose an active package.");
            }

            if (!package.CanBill(BillType.InPatient))
            {
                return Error.Field("packageId", $"{package.Name} is a package for out-patient visits.");
            }
        }

        if (account.PackageId == request.PackageId)
        {
            return Result.Success();
        }

        foreach (var old in await db.Set<Charge>()
                     .Where(c => c.AdmissionId == account.AdmissionId && c.SourceModule == ChargeService.PackageSource && c.Status != ChargeStatus.Cancelled)
                     .ToListAsync(ct))
        {
            var cancelled = await charges.CancelChargeAsync(old, "Package changed", ct);
            if (cancelled.IsFailure)
            {
                return cancelled;
            }
        }

        var assigned = account.AssignPackage(request.PackageId, time.GetUtcNow());
        if (assigned.IsFailure)
        {
            return assigned;
        }

        if (await db.Set<Bill>().AnyAsync(b => b.AdmissionId == account.AdmissionId && b.IsFinalBill && b.Status == BillStatus.Draft, ct))
        {
            await StageFinalBillAsync(account, ct);
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Interim bill (IPB-05) of the unbilled services up to a date, for self-paying patients during a long stay.
    /// Package and insured/credit admissions are billed once, on the final bill, so their split stays correct.
    /// </summary>
    public async Task<Result<Guid>> CreateInterimBillAsync(Guid id, CreateInterimBillRequest request, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        if (!account.IsActive)
        {
            return Error.Rule("billing.ip_account_inactive", "Interim bills are raised only during the stay.");
        }

        if (account.PackageId is not null)
        {
            return Error.Rule("billing.interim_package", "Package admissions are settled on the final bill.");
        }

        var terms = await coverage.ForAccountAsync(account, ct);
        if (terms.HasPayer)
        {
            return Error.Rule("billing.interim_insured", "Insured and credit admissions are billed on the final bill; use the running bill meanwhile.");
        }

        var cutoff = BillingTime.StartOfDay((request.UpTo ?? BillingTime.Today(time)).AddDays(1));
        var available = await AvailableChargesAsync(account.AdmissionId, cutoff, ct);
        if (available.Count == 0)
        {
            return Error.Rule("billing.nothing_to_bill", "There are no unbilled services up to that date.");
        }

        var bill = Bill.Draft(account.FacilityId, BillType.InPatient, false, account.PatientId, account.VisitId, account.AdmissionId, account.Id, terms);
        db.Add(bill);
        await bills.AttachAsync(bill, available, ct);
        await db.SaveChangesAsync(ct);
        return bill.Id;
    }

    /// <summary>
    /// Prepares (or refreshes) the draft final bill with every unbilled service, package cover and the payer split.
    /// Once the final bill is finalized, late services go on a supplementary bill for the patient.
    /// </summary>
    public async Task<Result<Guid>> PrepareFinalBillAsync(Guid id, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        if (account.Status == IpAccountStatus.Closed)
        {
            return Error.Rule("billing.ip_account_closed", "This in-patient account is closed.");
        }

        Bill bill;
        if (await HasFinalizedFinalBillAsync(account.AdmissionId, ct))
        {
            var late = await AvailableChargesAsync(account.AdmissionId, null, ct);
            if (late.Count == 0)
            {
                return Error.Rule("billing.nothing_to_bill", "The final bill is finalized and there are no new services.");
            }

            bill = Bill.Draft(
                account.FacilityId, BillType.InPatient, false, account.PatientId, account.VisitId, account.AdmissionId, account.Id,
                BillPayerTerms.SelfPay(account.PayerType, account.TariffPlanId));
            bill.Notes = "Supplementary bill for services charged after the final bill.";
            db.Add(bill);
            await bills.AttachAsync(bill, late, ct);
        }
        else
        {
            bill = await StageFinalBillAsync(account, ct);
            if (bill.Lines.Count == 0)
            {
                Discard(bill);
                return await db.Set<Charge>().AnyAsync(c => c.AdmissionId == account.AdmissionId && c.Status != ChargeStatus.Cancelled, ct)
                    ? Error.Rule("billing.billed_on_interim_bills",
                        "Every service of this stay is on an interim bill, so a final bill would hold nothing. Finalize any draft interim bill, then settle the advance.")
                    : Error.Rule("billing.nothing_to_bill", "No services have been charged to this admission yet.");
            }
        }

        await db.SaveChangesAsync(ct);
        return bill.Id;
    }

    /// <summary>
    /// Final settlement (IPB-08): adjusts the advance against the admission's open bills, oldest first; when nothing is
    /// due any more, the unused advance becomes a refund request (approved and paid like any refund). It follows the final
    /// bill; a stay with nothing for a final bill (its interim bills took every service, or none was charged) is settled
    /// once its discharge has begun, as more services may come until then.
    /// </summary>
    public async Task<Result<SettlementResult>> SettleAsync(Guid id, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        var finalBill = await db.Set<Bill>()
            .Where(b => b.AdmissionId == account.AdmissionId && b.IsFinalBill && b.Status != BillStatus.Draft && b.Status != BillStatus.Cancelled && b.Status != BillStatus.Refunded)
            .OrderByDescending(b => b.FinalizedAt)
            .FirstOrDefaultAsync(ct);
        if (finalBill is null)
        {
            if ((await BillingStatusProvider.PositionAsync(db, account.AdmissionId, ct)).UnbilledCount > 0)
            {
                return await HasDraftInterimBillAsync(account.AdmissionId, ct)
                    ? Error.Rule("billing.interim_bill_draft", "A draft interim bill holds services of this stay. Finalize it before settling the account.")
                    : Error.Rule("billing.final_bill_missing", "Finalize the final bill before settling the account.");
            }

            if (account.Status == IpAccountStatus.Open)
            {
                return Error.Rule("billing.discharge_not_initiated",
                    "Every service so far is on a finalized bill, and more may come during the stay. Settle the account once the discharge has been initiated.");
            }
        }

        var openBills = await db.Set<Bill>()
            .Where(b => b.AdmissionId == account.AdmissionId && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid))
            .OrderBy(b => b.FinalizedAt)
            .ToListAsync(ct);

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var pendingRefunds = await refunds.OutstandingForAccountAsync(account.Id, ct);
        var adjusted = 0m;
        foreach (var bill in openBills)
        {
            var amount = Math.Min(bill.DueAmount, account.DepositBalance - pendingRefunds);
            if (amount <= 0)
            {
                break;
            }

            var receipt = await receipts.AdjustAdvanceAsync(bill, account, amount, ct);
            if (receipt.IsFailure)
            {
                return receipt.Error!;
            }

            adjusted += amount;
        }

        var remainingDue = openBills.Sum(b => b.DueAmount);
        var excess = account.DepositBalance - pendingRefunds;
        Refund? refund = null;
        if (remainingDue == 0 && excess > 0)
        {
            refund = await refunds.StageRequestAsync(
                account.FacilityId, RefundPurpose.Deposit, account.PatientId, null, account.Id, excess,
                $"Unused advance after final settlement of admission {account.AdmissionNumber}", ct);
        }

        if (remainingDue == 0 && excess <= 0)
        {
            account.Close();
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SettlementResult(finalBill?.Id, adjusted, remainingDue, refund?.Id, refund?.Amount ?? 0m);
    }

    public async Task<Result> ApproveDischargeWithDuesAsync(Guid id, string reason, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), id);
        }

        if (currentUser.UserId is not { } approverId)
        {
            return BillingDb.SignInRequired;
        }

        var approved = account.ApproveDischargeWithDues(approverId, reason, time.GetUtcNow());
        if (approved.IsFailure)
        {
            return approved;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.discharge_with_dues.approved", $"Discharge with dues approved for admission {account.AdmissionNumber}: {reason.Trim()}")
        {
            PatientId = account.PatientId,
            EntityType = nameof(IpAccount),
            EntityId = account.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Creates or refreshes the draft final bill (does not save; safe inside an event handler).</summary>
    internal async Task<Bill> StageFinalBillAsync(IpAccount account, CancellationToken ct)
    {
        var bill = db.Set<Bill>().Local.FirstOrDefault(b => b.AdmissionId == account.AdmissionId && b.IsFinalBill && b.Status == BillStatus.Draft)
            ?? await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(b => b.Lines)
                .FirstOrDefaultAsync(b => b.AdmissionId == account.AdmissionId && b.IsFinalBill && b.Status == BillStatus.Draft, ct);
        var terms = await coverage.ForAccountAsync(account, ct);
        if (bill is null)
        {
            bill = Bill.Draft(account.FacilityId, BillType.InPatient, true, account.PatientId, account.VisitId, account.AdmissionId, account.Id, terms);
            db.Add(bill);
        }
        else
        {
            var lines = db.Entry(bill).Collection(b => b.Lines);
            if (!lines.IsLoaded && db.Entry(bill).State != EntityState.Added)
            {
                await lines.LoadAsync(ct);
            }

            // The payer may have changed since the draft was made (for example cash converted to cashless).
            bill.ApplyPayerTerms(terms);
            var isNonPayable = await coverage.NonPayableMatcherAsync(bill.HasPayer ? bill.PayerId : null, ct);
            foreach (var line in bill.Lines)
            {
                line.IsPayerPayable = !isNonPayable(line.ServiceItemId, line.Category);
            }
        }

        var packageCharge = await StagePackageChargeAsync(account, ct);
        await bills.AttachAsync(bill, await AvailableChargesAsync(account.AdmissionId, null, ct), ct);
        await ApplyPackageAsync(bill, account, packageCharge, ct);
        return bill;
    }

    /// <summary>Unbilled, unreserved charges of an admission — including ones staged in this unit of work.</summary>
    internal async Task<List<Charge>> AvailableChargesAsync(Guid admissionId, DateTimeOffset? before, CancellationToken ct)
    {
        var query = db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.AdmissionId == admissionId && c.Status == ChargeStatus.Unbilled && c.BillId == null);
        if (before is { } cutoff)
        {
            query = query.Where(c => c.ServiceDate < cutoff);
        }

        var available = await query.OrderBy(c => c.ServiceDate).ToListAsync(ct);
        available.AddRange(db.ChangeTracker.Entries<Charge>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Where(c => c.AdmissionId == admissionId && c.IsAvailableForBilling && (before is null || c.ServiceDate < before) && !available.Contains(c)));
        return available;
    }

    internal static string PackageReference(IpAccount account) =>
        $"{account.AdmissionId:N}:{account.PackageId:N}:{(account.PackageAssignedAt ?? account.AdmittedAt).ToUnixTimeSeconds()}";

    /// <summary>Drops a final bill drafted with nothing on it, unless it was saved before: an empty bill cannot be finalized.</summary>
    private void Discard(Bill bill)
    {
        if (db.Entry(bill).State == EntityState.Added)
        {
            db.Entry(bill).State = EntityState.Detached;
        }
    }

    /// <summary>Whether a draft interim bill holds services of the admission: they are not billed until it is finalized.</summary>
    private Task<bool> HasDraftInterimBillAsync(Guid admissionId, CancellationToken ct)
    {
        var drafts = db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(b => b.AdmissionId == admissionId && !b.IsFinalBill && b.Status == BillStatus.Draft)
            .Select(b => (Guid?)b.Id);
        return db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(c => c.AdmissionId == admissionId && c.Status == ChargeStatus.Unbilled && drafts.Contains(c.BillId), ct);
    }

    private Task<bool> HasFinalizedFinalBillAsync(Guid admissionId, CancellationToken ct) =>
        db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(
            b => b.AdmissionId == admissionId && b.IsFinalBill && b.Status != BillStatus.Draft && b.Status != BillStatus.Cancelled && b.Status != BillStatus.Refunded,
            ct);

    private async Task<Charge?> StagePackageChargeAsync(IpAccount account, CancellationToken ct)
    {
        if (account.PackageId is not { } packageId)
        {
            return null;
        }

        var package = await db.Set<Package>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return null;
        }

        var staged = await charges.StageAsync(new ChargeRequest
        {
            PatientId = account.PatientId,
            VisitId = account.VisitId,
            AdmissionId = account.AdmissionId,
            ServiceItemId = package.ServiceItemId,
            UnitPriceOverride = package.Price,
            Description = package.Name,
            SourceModule = ChargeService.PackageSource,
            SourceReference = PackageReference(account),
            PractitionerId = account.AttendingPractitionerId,
            ServiceDate = account.AdmittedAt,
        }, isFreeFollowUp: false, ct);
        if (staged.IsFailure)
        {
            logger.LogWarning("Package charge for admission {AdmissionNumber} was not posted: {Error}", account.AdmissionNumber, staged.Error!.Message);
            return null;
        }

        return staged.Value;
    }

    private async Task ApplyPackageAsync(Bill bill, IpAccount account, Charge? packageCharge, CancellationToken ct)
    {
        var package = account.PackageId is { } id
            ? await db.Set<Package>().AsNoTracking().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == id, ct)
            : null;
        if (package is null)
        {
            bill.ApplyPackageCoverage(new Dictionary<Guid, decimal>());
            bill.Recalculate();
            return;
        }

        var usages = bill.Lines
            .Where(l => l.ChargeId != packageCharge?.Id && l.Category != ServiceCategory.Package)
            .Select(l => new PackageUsage(l.Id, l.ServiceItemId, l.Category, l.Quantity, l.UnitPrice, l.GrossAmount, l.ServiceDate));
        var outcome = PackageCalculator.Allocate(package.Price, package.Rules(), usages);
        bill.ApplyPackageCoverage(outcome.Lines.ToDictionary(a => a.Key, a => a.Covered));
        bill.Recalculate();
    }

    private async Task<PackageEvaluation?> EvaluatePackageAsync(IpAccount account, IReadOnlyList<Charge> services, CancellationToken ct)
    {
        if (account.PackageId is not { } packageId)
        {
            return null;
        }

        var package = await db.Set<Package>().AsNoTracking().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return null;
        }

        var taxRate = await db.Set<ServiceItem>().Where(s => s.Id == package.ServiceItemId).Select(s => s.TaxRate).FirstOrDefaultAsync(ct);
        var outcome = PackageCalculator.Allocate(
            package.Price, package.Rules(), services.Select(c => new PackageUsage(c.Id, c.ServiceItemId, c.Category, c.Quantity, c.UnitPrice, c.GrossAmount, c.ServiceDate)));
        var byId = services.ToDictionary(c => c.Id);
        var status = new PackageStatus(
            package.Id, package.Code, package.Name, outcome.PackagePrice, outcome.Actuals, outcome.Covered, outcome.Excess, outcome.Variance,
            outcome.Lines.Select(l => new PackageLineStatus(l.Key, byId[l.Key].ServiceCode, byId[l.Key].Description, l.Covered + l.Excess, l.Covered, l.Excess)).ToList());
        return new PackageEvaluation(package.Price, taxRate, outcome, status);
    }

    /// <summary>What the stay would cost if billed now (package cover and payer split applied, no staff discounts).</summary>
    private async Task<BillTotals> EstimateAsync(IReadOnlyList<Charge> services, PackageEvaluation? package, BillPayerTerms terms, CancellationToken ct)
    {
        var isNonPayable = await coverage.NonPayableMatcherAsync(terms.HasPayer ? terms.PayerId : null, ct);
        var covered = package?.Outcome.Lines.ToDictionary(l => l.Key, l => l.Covered) ?? new Dictionary<Guid, decimal>();
        var inputs = services
            .Select(c => new BillLineInput(c.GrossAmount, covered.GetValueOrDefault(c.Id), c.DiscountPercent, 0m, c.IsDiscountAllowed, c.TaxRate, !isNonPayable(c.ServiceItemId, c.Category)))
            .ToList();
        if (package is not null)
        {
            inputs.Add(new BillLineInput(package.Price, 0m, 0m, 0m, false, package.TaxRate, true));
        }

        return BillTotalsCalculator.Calculate(inputs, BillDiscountInput.None, new SplitTerms(terms.HasPayer, terms.CoPayPercent, terms.CoverageLimit));
    }

    private sealed record PackageEvaluation(decimal Price, decimal TaxRate, PackageOutcome Outcome, PackageStatus Status);
}
