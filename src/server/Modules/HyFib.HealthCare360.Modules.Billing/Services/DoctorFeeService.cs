using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Doctor fee accounting (DFA-01..05): accrues each doctor's share of billed lines when a bill is finalized (or fully
/// paid, per setting), reverses it when the bill is cancelled, and turns a period's unpaid accruals into a payout with
/// deductions and TDS that a second person approves before it is paid.
/// </summary>
public sealed class DoctorFeeService(HospitalDbContext db, INumberSeries numbers, ISettingsService settings, IAuditLogger audit, ICurrentUser currentUser, TimeProvider time)
{
    /// <summary>"finalized" (default: fee earned when billed) or "paid" (earned when the patient's share is fully paid).</summary>
    public const string TriggerSetting = "doctorfees.accrual.trigger";

    /// <summary>TDS percentage on professional fees (section 194J); default 10.</summary>
    public const string TdsSetting = "doctorfees.tds.percent";

    internal async Task<bool> AccruesOnPaymentAsync(Guid facilityId, CancellationToken ct) =>
        string.Equals(await settings.GetAsync<string>(TriggerSetting, facilityId, ct), "paid", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accrues the doctors' shares of a finalized bill (idempotent per bill line). Does not save.</summary>
    internal async Task AccrueAsync(Bill bill, CancellationToken ct)
    {
        if (bill.BillNumber is null)
        {
            return;
        }

        var lines = bill.Lines.Where(l => l.PractitionerId is not null && l.TaxableAmount > 0).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var lineIds = lines.Select(l => l.Id).ToList();
        var accrued = await db.Set<DoctorFeeAccrual>().IgnoreQueryFilters()
            .Where(a => lineIds.Contains(a.BillLineId) && a.ReversalOfId == null)
            .Select(a => a.BillLineId)
            .ToListAsync(ct);
        accrued.AddRange(db.Set<DoctorFeeAccrual>().Local.Where(a => a.BillId == bill.Id && a.ReversalOfId == null).Select(a => a.BillLineId));

        var practitionerIds = lines.Select(l => l.PractitionerId!.Value).Distinct().ToList();
        var rules = (await db.Set<DoctorShareRule>().AsNoTracking()
                .Where(r => r.IsActive && (r.PractitionerId == null || practitionerIds.Contains(r.PractitionerId.Value)))
                .ToListAsync(ct))
            .Select(r => r.ToView())
            .ToList();
        var today = BillingTime.Today(time);

        foreach (var line in lines.Where(l => !accrued.Contains(l.Id)))
        {
            var practitionerId = line.PractitionerId!.Value;
            var serviceDate = BillingTime.LocalDate(line.ServiceDate);
            var share = DoctorShareCalculator.Calculate(
                rules, new ShareLine(practitionerId, line.ServiceItemId, line.Category, bill.PayerType, line.Quantity, line.TaxableAmount, serviceDate));
            if (share.Rule is null || share.Amount <= 0)
            {
                continue;
            }

            db.Add(new DoctorFeeAccrual
            {
                FacilityId = bill.FacilityId,
                PractitionerId = practitionerId,
                BillId = bill.Id,
                BillLineId = line.Id,
                BillNumber = bill.BillNumber,
                PatientId = bill.PatientId,
                ServiceItemId = line.ServiceItemId,
                ServiceCode = line.ServiceCode,
                Description = line.Description,
                Category = line.Category,
                PayerType = bill.PayerType,
                ServiceDate = serviceDate,
                AccruedOn = today,
                BasisAmount = line.TaxableAmount,
                RuleId = share.Rule.Id,
                Method = share.Rule.Method,
                Rate = share.Rule.Value,
                ShareAmount = share.Amount,
                Status = AccrualStatus.Accrued,
            });
        }
    }

    /// <summary>
    /// Reverses the accruals of a cancelled bill with dated negative entries (so the Tally journal reverses too). Unpaid
    /// pairs cancel out; a reversal of an accrual already in a payout is recovered from the doctor's next payout.
    /// </summary>
    internal async Task ReverseAsync(Guid billId, CancellationToken ct)
    {
        var accruals = await db.Set<DoctorFeeAccrual>().IgnoreQueryFilters()
            .Where(a => a.BillId == billId && a.ReversalOfId == null)
            .ToListAsync(ct);
        var alreadyReversed = await db.Set<DoctorFeeAccrual>().IgnoreQueryFilters()
            .Where(a => a.BillId == billId && a.ReversalOfId != null)
            .Select(a => a.ReversalOfId!.Value)
            .ToListAsync(ct);
        var today = BillingTime.Today(time);

        foreach (var original in accruals.Where(a => !alreadyReversed.Contains(a.Id)))
        {
            var unpaid = original.Status == AccrualStatus.Accrued;
            db.Add(new DoctorFeeAccrual
            {
                FacilityId = original.FacilityId,
                PractitionerId = original.PractitionerId,
                BillId = original.BillId,
                BillLineId = original.BillLineId,
                BillNumber = original.BillNumber,
                PatientId = original.PatientId,
                ServiceItemId = original.ServiceItemId,
                ServiceCode = original.ServiceCode,
                Description = $"Reversal: {original.Description}",
                Category = original.Category,
                PayerType = original.PayerType,
                ServiceDate = original.ServiceDate,
                AccruedOn = today,
                BasisAmount = -original.BasisAmount,
                RuleId = original.RuleId,
                Method = original.Method,
                Rate = original.Rate,
                ShareAmount = -original.ShareAmount,
                Status = unpaid ? AccrualStatus.Reversed : AccrualStatus.Accrued,
                ReversalOfId = original.Id,
            });
            if (unpaid)
            {
                original.Status = AccrualStatus.Reversed;
            }
        }
    }

    public async Task<Result<Guid>> SaveRuleAsync(Guid? id, SaveShareRuleRequest request, CancellationToken ct)
    {
        if (request.PractitionerId is { } practitionerId && !await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId, ct))
        {
            return Error.Field("practitionerId", "Choose a practitioner.");
        }

        if (request.ServiceItemId is { } serviceItemId && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == serviceItemId, ct))
        {
            return Error.Field("serviceItemId", "Choose a service.");
        }

        DoctorShareRule rule;
        if (id is { } existingId)
        {
            var found = await db.Set<DoctorShareRule>().FirstOrDefaultAsync(r => r.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(DoctorShareRule), existingId);
            }

            db.SetExpectedVersion(found, request.Version ?? 0);
            rule = found;
        }
        else
        {
            rule = new DoctorShareRule();
            db.Add(rule);
        }

        rule.PractitionerId = request.PractitionerId;
        rule.ServiceItemId = request.ServiceItemId;
        rule.Category = request.Category;
        rule.PayerType = request.PayerType;
        rule.Method = request.Method;
        rule.Value = request.Value;
        rule.EffectiveFrom = request.EffectiveFrom;
        rule.EffectiveTo = request.EffectiveTo;
        rule.IsActive = request.IsActive;
        rule.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        await db.SaveChangesAsync(ct);
        return rule.Id;
    }

    /// <summary>Closes the period for a doctor: unpaid accruals in it are locked into a draft payout.</summary>
    public async Task<Result<Guid>> CreatePayoutAsync(CreatePayoutRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired;
        }

        if (!await db.Set<Practitioner>().AnyAsync(p => p.Id == request.PractitionerId, ct))
        {
            return Error.Field("practitionerId", "Choose a practitioner.");
        }

        var accruals = await db.Set<DoctorFeeAccrual>()
            .Where(a => a.PractitionerId == request.PractitionerId && a.Status == AccrualStatus.Accrued && a.PayoutId == null
                        && a.AccruedOn >= request.PeriodFrom && a.AccruedOn <= request.PeriodTo)
            .ToListAsync(ct);
        if (accruals.Count == 0)
        {
            return Error.Rule("doctorfees.nothing_to_pay", "There are no unpaid fees for this doctor in the period.");
        }

        var deductions = request.Deductions ?? [];
        var tdsPercent = request.TdsPercent ?? await settings.GetAsync<decimal?>(TdsSetting, facilityId, ct) ?? 10m;
        var amounts = PayoutCalculator.Calculate(accruals.Sum(a => a.ShareAmount), deductions.Sum(d => d.Amount), tdsPercent);
        if (amounts.Net < 0)
        {
            return Error.Rule("doctorfees.negative_payout", "Deductions and TDS exceed the fees of this period.");
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var payout = new DoctorPayout
        {
            FacilityId = facilityId,
            PayoutNumber = await numbers.NextAsync(BillingNumberSeries.DoctorPayout, facilityId, ct),
            PractitionerId = request.PractitionerId,
            PeriodFrom = request.PeriodFrom,
            PeriodTo = request.PeriodTo,
            Status = PayoutStatus.Draft,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Deductions = deductions.Select(d => new DoctorPayoutDeduction { Description = d.Description.Trim(), Amount = Money.Round(d.Amount) }).ToList(),
        };
        payout.ApplyAmounts(amounts);
        db.Add(payout);
        foreach (var accrual in accruals)
        {
            accrual.Status = AccrualStatus.InPayout;
            accrual.PayoutId = payout.Id;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return payout.Id;
    }

    public async Task<Result> ApprovePayoutAsync(Guid id, uint version, CancellationToken ct)
    {
        var payout = await db.Set<DoctorPayout>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payout is null)
        {
            return Error.NotFound(nameof(DoctorPayout), id);
        }

        if (currentUser.UserId is not { } approverId)
        {
            return BillingDb.SignInRequired;
        }

        db.SetExpectedVersion(payout, version);
        var approved = payout.Approve(approverId, time.GetUtcNow());
        if (approved.IsFailure)
        {
            return approved;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> PayPayoutAsync(Guid id, PayPayoutRequest request, CancellationToken ct)
    {
        var payout = await db.Set<DoctorPayout>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payout is null)
        {
            return Error.NotFound(nameof(DoctorPayout), id);
        }

        db.SetExpectedVersion(payout, request.Version);
        var paid = payout.MarkPaid(request.Mode, request.Reference, time.GetUtcNow());
        if (paid.IsFailure)
        {
            return paid;
        }

        foreach (var accrual in await db.Set<DoctorFeeAccrual>().Where(a => a.PayoutId == payout.Id).ToListAsync(ct))
        {
            accrual.Status = AccrualStatus.Paid;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "doctorfees.payout.paid", $"Paid doctor payout {payout.PayoutNumber}: net ₹{Money.FormatInr(payout.NetAmount)}")
        {
            EntityType = nameof(DoctorPayout),
            EntityId = payout.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Deletes a draft payout and returns its accruals to the unpaid pool.</summary>
    public async Task<Result> DiscardPayoutAsync(Guid id, uint version, CancellationToken ct)
    {
        var payout = await db.Set<DoctorPayout>().Include(p => p.Deductions).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payout is null)
        {
            return Error.NotFound(nameof(DoctorPayout), id);
        }

        if (payout.Status != PayoutStatus.Draft)
        {
            return Error.Rule("doctorfees.payout_not_draft", "Only draft payouts can be discarded.");
        }

        db.SetExpectedVersion(payout, version);
        foreach (var accrual in await db.Set<DoctorFeeAccrual>().Where(a => a.PayoutId == payout.Id).ToListAsync(ct))
        {
            accrual.Status = AccrualStatus.Accrued;
            accrual.PayoutId = null;
        }

        db.Remove(payout);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
