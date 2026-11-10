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
public sealed class DoctorFeeService(
    HospitalDbContext db, INumberSeries numbers, ISettingsService settings, IAuditLogger audit, ICurrentUser currentUser, ShareRuleApprovals ruleApprovals, TimeProvider time,
    DoctorTaxService tax)
{
    /// <summary>"finalized" (default: fee earned when billed) or "paid" (earned when the patient's share is fully paid).</summary>
    public const string TriggerSetting = "doctorfees.accrual.trigger";

    /// <summary>
    /// The s.194J rate on professional fees for a doctor with a PAN, when set; otherwise the year's rate in
    /// <c>ProfessionalFeeTds</c> (10 %). The threshold and the 20 % without a PAN (s.206AA) apply either way.
    /// </summary>
    public const string TdsSetting = "doctorfees.tds.percent";

    internal async Task<bool> AccruesOnPaymentAsync(Guid facilityId, CancellationToken ct) =>
        string.Equals(await settings.GetAsync<string>(TriggerSetting, facilityId, ct), "paid", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accrues the doctors' shares of a finalized bill (idempotent per bill line: a line accrues again only once its
    /// accrual was reversed, as when a bill paid, then owing again, is paid again). Does not save.
    /// </summary>
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
        // Every accrual and reversal of these lines, saved or about to be.
        var entries = (await db.Set<DoctorFeeAccrual>().IgnoreQueryFilters().AsNoTracking()
                .Where(a => lineIds.Contains(a.BillLineId))
                .Select(a => new FeeEntry(a.Id, a.BillLineId, a.ReversalOfId, a.Occurrence))
                .ToListAsync(ct))
            .Concat(db.Set<DoctorFeeAccrual>().Local.Where(a => lineIds.Contains(a.BillLineId)).Select(a => new FeeEntry(a.Id, a.BillLineId, a.ReversalOfId, a.Occurrence)))
            .DistinctBy(e => e.Id)
            .ToList();
        var reversed = entries.Where(e => e.ReversalOfId is not null).Select(e => e.ReversalOfId!.Value).ToHashSet();
        var originals = entries.Where(e => e.ReversalOfId is null).ToList();
        var accrued = originals.Where(e => !reversed.Contains(e.Id)).Select(e => e.BillLineId).ToHashSet();
        var lastOccurrence = originals.GroupBy(e => e.BillLineId).ToDictionary(g => g.Key, g => g.Max(e => e.Occurrence));

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
                Occurrence = lastOccurrence.GetValueOrDefault(line.Id) + 1,
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
                Occurrence = original.Occurrence,
            });
            if (unpaid)
            {
                original.Status = AccrualStatus.Reversed;
            }
        }
    }

    /// <summary>
    /// Saves a sharing rule (maker-checker): its notes at once, and its terms only when a second person approves them
    /// (<see cref="ShareRuleApprovals"/>). A new rule is saved with the terms asked, out of use until they are approved.
    /// </summary>
    public async Task<Result<SavedShareRule>> SaveRuleAsync(Guid? id, SaveShareRuleRequest request, CancellationToken ct)
    {
        if (request.PractitionerId is { } practitionerId && !await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId, ct))
        {
            return Error.Field("practitionerId", "Choose a practitioner.");
        }

        if (request.ServiceItemId is { } serviceItemId && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == serviceItemId, ct))
        {
            return Error.Field("serviceItemId", "Choose a service.");
        }

        var asked = ShareRuleTerms.Of(request);
        DoctorShareRule rule;
        ShareRuleTerms before;
        if (id is { } existingId)
        {
            var found = await db.Set<DoctorShareRule>().FirstOrDefaultAsync(r => r.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(DoctorShareRule), existingId);
            }

            db.SetExpectedVersion(found, request.Version ?? 0);
            rule = found;
            before = ShareRuleTerms.Of(found);
        }
        else
        {
            rule = new DoctorShareRule();
            before = asked.OutOfUse();
            before.ApplyTo(rule);
            db.Add(rule);
        }

        rule.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (asked == before)
        {
            await db.SaveChangesAsync(ct);
            return new SavedShareRule(rule.Id, null);
        }

        // One change of a rule waits at a time: saving what already waits does not ask twice, and another change is refused.
        if (id is not null && (await ruleApprovals.PendingAsync([rule.Id], ct)).GetValueOrDefault(rule.Id) is { } waiting)
        {
            if (waiting.Asked != asked)
            {
                return Error.Conflict("approval.already_requested", "A change of this rule is already waiting for approval. Withdraw it, or wait for its decision, before asking for another.");
            }

            await db.SaveChangesAsync(ct);
            return new SavedShareRule(rule.Id, null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        var requested = await ruleApprovals.AskAsync(rule, before, asked, id is null, request.Reason, ct);
        if (requested.IsFailure)
        {
            return requested.Error!;
        }

        await transaction.CommitAsync(ct);
        return new SavedShareRule(rule.Id, requested.Value);
    }

    /// <summary>
    /// Deletes a rule out of use, which shares nothing, at once. A rule in use is not deleted: taking it out of use, which a
    /// second person approves, comes first.
    /// </summary>
    public async Task<Result> DeleteRuleAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.Set<DoctorShareRule>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return Error.NotFound(nameof(DoctorShareRule), id);
        }

        if (rule.IsActive)
        {
            return Error.Conflict("doctorfees.rule_in_use",
                "A rule in use is not deleted: ask to take it out of use, which a second person approves, and delete it then.");
        }

        db.Remove(rule);
        await db.SaveChangesAsync(ct);
        return Result.Success();
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
        var gross = Money.Round(accruals.Sum(a => a.ShareAmount));
        var tdsRate = await settings.GetAsync<decimal?>(TdsSetting, facilityId, ct);
        var fee = await tax.PayoutTdsAsync(request.PractitionerId, gross, tdsRate, request.TdsPercent, BillingTime.Today(time), ct);
        if (fee.IsFailure)
        {
            return fee.Error!;
        }

        var deducted = Money.Round(Math.Max(0, deductions.Sum(d => d.Amount)));
        var amounts = new PayoutAmounts(gross, deducted, fee.Value.Tax.RatePercent, fee.Value.Tax.Tds, gross - fee.Value.Tax.Tds - deducted);
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
        payout.TdsBasis = fee.Value.Tax.Basis;
        payout.TdsRemark = fee.Value.Tax.Remark;
        payout.TdsPanCipher = fee.Value.PanCipher;
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

    /// <summary>An accrual or a reversal of a bill line, as far as accruing it again needs.</summary>
    private sealed record FeeEntry(Guid Id, Guid BillLineId, Guid? ReversalOfId, int Occurrence);
}
