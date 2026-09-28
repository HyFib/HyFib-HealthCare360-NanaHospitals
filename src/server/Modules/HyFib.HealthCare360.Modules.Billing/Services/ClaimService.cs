using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Insurance/corporate claims (INS-05..07, MB-03): raised on the payer share of a finalized bill with ICD/procedure
/// coding and documents; settlements are recorded as payer receipts (mode Insurance) and deductions reduce the bill's
/// payer balance, which drives the receivable ageing.
/// </summary>
public sealed class ClaimService(HospitalDbContext db, INumberSeries numbers, ReceiptService receipts, IAuditLogger audit, TimeProvider time)
{
    public const string Series = "insurance.claim";
    public const string Format = "{FAC}CL{YY}{SEQ:6}";

    public async Task<Result<Guid>> CreateAsync(CreateClaimRequest request, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().FirstOrDefaultAsync(b => b.Id == request.BillId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), request.BillId);
        }

        if (bill.Status is BillStatus.Draft or BillStatus.Cancelled or BillStatus.Refunded)
        {
            return Error.Rule("insurance.claim_bill_not_final", "Claims are raised on finalized bills.");
        }

        if (bill.PayerId is not { } payerId || bill.PayerOutstanding <= 0)
        {
            return Error.Rule("insurance.claim_no_payer_share", "This bill has no payer share left to claim.");
        }

        if (await db.Set<InsuranceClaim>().AnyAsync(c => c.BillId == bill.Id && c.Status != ClaimStatus.Rejected, ct))
        {
            return Error.Conflict("insurance.claim_exists", "A claim already exists for this bill.");
        }

        var claimed = Money.Round(request.ClaimedAmount ?? bill.PayerOutstanding);
        if (claimed <= 0 || claimed > bill.PayerOutstanding)
        {
            return Error.Field("claimedAmount", $"Claim between ₹1 and ₹{Money.FormatInr(bill.PayerOutstanding)}.");
        }

        var preAuth = bill.PreAuthorizationId is { } preAuthId
            ? await db.Set<PreAuthorization>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == preAuthId, ct)
            : null;
        var tpaId = preAuth?.TpaId ?? (bill.PolicyId is { } policyId
            ? await db.Set<PatientPolicy>().Where(p => p.Id == policyId).Select(p => p.TpaId).FirstOrDefaultAsync(ct)
            : null);

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var claim = new InsuranceClaim
        {
            FacilityId = bill.FacilityId,
            ClaimNumber = await numbers.NextAsync(Series, bill.FacilityId, Format, NumberResetPolicy.Yearly, ct),
            BillId = bill.Id,
            PatientId = bill.PatientId,
            PayerId = payerId,
            TpaId = tpaId,
            PolicyId = bill.PolicyId,
            PreAuthorizationId = bill.PreAuthorizationId,
            ClaimedAmount = claimed,
            DiagnosisCodes = Clean(request.DiagnosisCodes) ?? preAuth?.DiagnosisCodes,
            ProcedureCodes = Clean(request.ProcedureCodes) ?? preAuth?.ProcedureCodes,
            DocumentFileIds = request.DocumentFileIds?.Distinct().ToList() ?? preAuth?.DocumentFileIds.ToList() ?? [],
            PayerClaimReference = Clean(request.PayerClaimReference),
            Status = ClaimStatus.Submitted,
            SubmittedAt = time.GetUtcNow(),
        };
        db.Add(claim);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return claim.Id;
    }

    public async Task<Result> UpdateCodingAsync(Guid id, UpdateClaimCodingRequest request, CancellationToken ct)
    {
        var claim = await LoadAsync(id, ct);
        if (claim is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), id);
        }

        if (claim.IsClosed)
        {
            return Error.Rule("insurance.claim_closed", "Settled and rejected claims cannot be changed.");
        }

        db.SetExpectedVersion(claim, request.Version);
        claim.UpdateCoding(request.DiagnosisCodes, request.ProcedureCodes);
        if (request.DocumentFileIds is not null)
        {
            claim.DocumentFileIds = request.DocumentFileIds.Distinct().ToList();
        }

        claim.PayerClaimReference = Clean(request.PayerClaimReference) ?? claim.PayerClaimReference;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result> RaiseQueryAsync(Guid id, string text, CancellationToken ct) => ChangeAsync(id, (c, now) => c.RaiseQuery(text, now), ct);

    public Task<Result> AnswerQueryAsync(Guid id, string text, CancellationToken ct) => ChangeAsync(id, (c, now) => c.AnswerQuery(text, now), ct);

    public Task<Result> ApproveAsync(Guid id, decimal amount, CancellationToken ct) => ChangeAsync(id, (c, now) => c.Approve(amount, now), ct);

    /// <summary>Records a settlement tranche: the money becomes a payer receipt, deductions are disallowed on the bill.</summary>
    public async Task<Result> SettleAsync(Guid id, SettleClaimRequest request, CancellationToken ct)
    {
        var claim = await LoadAsync(id, ct);
        if (claim is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), id);
        }

        var bill = await db.Set<Bill>().FirstAsync(b => b.Id == claim.BillId, ct);
        var settled = claim.Settle(
            request.Amount, request.Deductions ?? [], request.UtrNumber, request.Reference, request.SettledOn ?? BillingTime.Today(time), time.GetUtcNow());
        if (settled.IsFailure)
        {
            return settled.Error!;
        }

        var settlement = settled.Value;
        var applied = bill.ApplyPayerSettlement(settlement.Amount, settlement.DeductionAmount);
        if (applied.IsFailure)
        {
            return applied;
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        if (settlement.Amount > 0)
        {
            var receipt = await receipts.RecordPayerSettlementAsync(bill, claim, settlement.Amount, settlement.UtrNumber, ct);
            settlement.ReceiptId = receipt.Id;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "insurance.claim.settled",
            $"{claim.ClaimNumber}: settled ₹{Money.FormatInr(settlement.Amount)}, deductions ₹{Money.FormatInr(settlement.DeductionAmount)}")
        {
            PatientId = claim.PatientId,
            EntityType = nameof(InsuranceClaim),
            EntityId = claim.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    /// <summary>Rejection disallows the unpaid balance on the bill (recovered from the patient by a new bill or written off).</summary>
    public async Task<Result> RejectAsync(Guid id, string reason, CancellationToken ct)
    {
        var claim = await LoadAsync(id, ct);
        if (claim is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), id);
        }

        var rejected = claim.Reject(reason, BillingTime.Today(time), time.GetUtcNow());
        if (rejected.IsFailure)
        {
            return rejected.Error!;
        }

        if (rejected.Value is { DeductionAmount: > 0 } disallowance)
        {
            var bill = await db.Set<Bill>().FirstAsync(b => b.Id == claim.BillId, ct);
            var applied = bill.ApplyPayerSettlement(0m, Math.Min(disallowance.DeductionAmount, bill.PayerOutstanding));
            if (applied.IsFailure)
            {
                return applied;
            }
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "insurance.claim.rejected", $"{claim.ClaimNumber} rejected: {reason.Trim()}")
        {
            PatientId = claim.PatientId,
            EntityType = nameof(InsuranceClaim),
            EntityId = claim.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Payer receivables (unsettled payer shares of finalized bills) aged by bill date as of a date.</summary>
    public async Task<ReceivablesAgeingReport> AgeingAsync(DateOnly asOf, CancellationToken ct)
    {
        var end = BillingTime.StartOfDay(asOf.AddDays(1));
        var rows = await db.Set<Bill>().AsNoTracking()
            .Where(b => b.PayerId != null && b.FinalizedAt != null && b.FinalizedAt < end
                        && b.Status != BillStatus.Draft && b.Status != BillStatus.Cancelled && b.Status != BillStatus.Refunded
                        && b.PayerAmount - b.PayerSettledAmount - b.PayerDisallowedAmount > 0)
            .Select(b => new { PayerId = b.PayerId!.Value, Outstanding = b.PayerAmount - b.PayerSettledAmount - b.PayerDisallowedAmount, FinalizedAt = b.FinalizedAt!.Value })
            .ToListAsync(ct);

        var buckets = ReceivablesAgeing.ByPayer(rows.Select(r => new ReceivableItem(r.PayerId, r.Outstanding, BillingTime.LocalDate(r.FinalizedAt))), asOf);
        var payerIds = buckets.Keys.ToList();
        var payers = await db.Set<Payer>().AsNoTracking().Where(p => payerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.Name, p.Category, p.CreditDays }, ct);

        var lines = buckets
            .Select(kv =>
            {
                var payer = payers.GetValueOrDefault(kv.Key);
                return new AgeingRow(
                    kv.Key, payer?.Name ?? "Unknown payer", payer?.Category, payer?.CreditDays ?? 0, kv.Value.Days0To30, kv.Value.Days31To60,
                    kv.Value.Days61To90, kv.Value.Over90, kv.Value.Total);
            })
            .OrderByDescending(r => r.Total)
            .ToList();

        return new ReceivablesAgeingReport(
            asOf, lines, lines.Sum(l => l.Days0To30), lines.Sum(l => l.Days31To60), lines.Sum(l => l.Days61To90), lines.Sum(l => l.Over90), lines.Sum(l => l.Total));
    }

    private async Task<Result> ChangeAsync(Guid id, Func<InsuranceClaim, DateTimeOffset, Result> change, CancellationToken ct)
    {
        var claim = await LoadAsync(id, ct);
        if (claim is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), id);
        }

        var result = change(claim, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private Task<InsuranceClaim?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Set<InsuranceClaim>().Include(c => c.Settlements).Include(c => c.Deductions).FirstOrDefaultAsync(c => c.Id == id, ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
