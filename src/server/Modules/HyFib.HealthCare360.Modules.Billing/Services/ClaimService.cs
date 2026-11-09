using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Insurance/corporate claims (INS-05..07, MB-03): raised on the payer share of a finalized bill with ICD/procedure
/// coding and documents; settlements are recorded as payer receipts (mode Insurance) and deductions reduce the bill's
/// payer balance, which drives the receivable ageing. What the hospital gives up on a claim (the insurer's deductions, or
/// the whole unpaid claim when it is rejected) is written off at once only up to the facility's threshold; above it the
/// write-off waits for someone else who approves insurance write-offs (MAKER-CHECKER.md, slice 12).
/// </summary>
public sealed class ClaimService(
    HospitalDbContext db,
    INumberSeries numbers,
    ReceiptService receipts,
    IVerifiedCodingSource verifiedCoding,
    IApprovals approvals,
    ISettingsService settings,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>An insurance write-off above the threshold, waiting for a second person.</summary>
    public const string WriteOffKind = "insurance.claim_writeoff";

    /// <summary>
    /// The amount (₹) up to which a claim's write-off (deductions on a settlement, or the unpaid claim on a rejection) is
    /// posted at once; a larger one waits for approval. Below 0 acts as 0. Loosening it waits for a second administrator
    /// (SettingGuard).
    /// </summary>
    public const string WriteOffApprovalAboveSetting = "billing.claimWriteOff.approvalAbove";

    /// <summary>
    /// ₹0: every write-off waits. An insurer's deduction is money the hospital never collects, from a payer the patient
    /// does not see, recorded by one desk; deductions and rejections are few a day, so a second look at each costs little.
    /// </summary>
    public const decimal DefaultWriteOffApprovalAbove = 0m;

    /// <summary>The facility's write-off threshold (<see cref="WriteOffApprovalAboveSetting"/>).</summary>
    public async Task<decimal> WriteOffApprovalAboveAsync(Guid? facilityId, CancellationToken ct) =>
        Math.Max(await settings.GetAsync<decimal?>(WriteOffApprovalAboveSetting, facilityId, ct) ?? DefaultWriteOffApprovalAbove, 0m);

    /// <summary>When a write-off of each of these claims was asked for and still waits.</summary>
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> WriteOffsAskedAsync(IReadOnlyCollection<Guid> claimIds, CancellationToken ct) =>
        approvals.WaitingSinceAsync(WriteOffKind, nameof(InsuranceClaim), claimIds, ct);

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
            ? await db.Set<PreAuthorization>().AsNoTracking().Include(p => p.Procedures).FirstOrDefaultAsync(p => p.Id == preAuthId, ct)
            : null;
        var tpaId = preAuth?.TpaId ?? (bill.PolicyId is { } policyId
            ? await db.Set<PatientPolicy>().Where(p => p.Id == policyId).Select(p => p.TpaId).FirstOrDefaultAsync(ct)
            : null);
        var verified = await VerifiedCodingAsync(bill.PatientId, bill.AdmissionId, bill.VisitId, ct);
        var (diagnosisCodes, procedureCodes) = InitialCoding(verified, preAuth);

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var claim = new InsuranceClaim
        {
            FacilityId = bill.FacilityId,
            ClaimNumber = await numbers.NextAsync(BillingNumberSeries.InsuranceClaim, bill.FacilityId, ct),
            BillId = bill.Id,
            PatientId = bill.PatientId,
            PayerId = payerId,
            TpaId = tpaId,
            PolicyId = bill.PolicyId,
            PreAuthorizationId = bill.PreAuthorizationId,
            ClaimedAmount = claimed,
            DiagnosisCodes = Clean(request.DiagnosisCodes) ?? diagnosisCodes,
            ProcedureCodes = Clean(request.ProcedureCodes) ?? procedureCodes,
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

    /// <summary>
    /// Records a settlement tranche: the money becomes a payer receipt, deductions are disallowed on the bill. Deductions
    /// above the threshold are not posted: the money is (it was received), and the deductions wait for approval, the claim
    /// staying open for them meanwhile.
    /// </summary>
    public async Task<Result<ClaimWriteOffOutcome>> SettleAsync(Guid id, SettleClaimRequest request, CancellationToken ct)
    {
        var claim = await LoadAsync(id, ct);
        if (claim is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), id);
        }

        if (claim.Version != request.Version)
        {
            return Error.ConcurrencyConflict;
        }

        var deductions = request.Deductions ?? [];
        var valid = claim.ValidateSettlement(request.Amount, deductions, request.UtrNumber);
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        var settledOn = request.SettledOn ?? BillingTime.Today(time);
        var deducted = deductions.Sum(d => Money.Round(d.Amount));
        var bill = await db.Set<Bill>().FirstAsync(b => b.Id == claim.BillId, ct);
        var waits = ClaimWriteOffs.NeedsApproval(deducted, await WriteOffApprovalAboveAsync(claim.FacilityId, ct));
        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        if (!waits || request.Amount > 0)
        {
            var posted = await PostSettlementAsync(claim, bill, request.Amount, waits ? [] : deductions, request.UtrNumber, request.Reference, settledOn, ct);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            await db.SaveChangesAsync(ct);
        }

        if (!waits)
        {
            await transaction.CommitAsync(ct);
            return new ClaimWriteOffOutcome(null);
        }

        var asked = await AskWriteOffAsync(
            claim, new ClaimWriteOffPayload(claim.Id, ClaimWriteOffKind.Deductions, [.. deductions], request.Reference, settledOn, null, deducted),
            $"Write off ₹{Money.FormatInr(deducted)} the insurer deducted from claim {claim.ClaimNumber}",
            [.. deductions.Select(d => new ApprovalDetail("Deduction", $"₹{Money.FormatInr(Money.Round(d.Amount))}: {d.Reason.Trim()}"))],
            string.Join("; ", deductions.Select(d => d.Reason.Trim())), ct);
        if (asked.IsFailure)
        {
            return asked.Error!;
        }

        await transaction.CommitAsync(ct);
        return new ClaimWriteOffOutcome(asked.Value);
    }

    /// <summary>
    /// Rejection disallows the unpaid balance on the bill (recovered from the patient by a new bill or written off). Above
    /// the threshold nothing changes until someone else approves: the claim stays open, the insurer still owes it.
    /// </summary>
    public async Task<Result<ClaimWriteOffOutcome>> RejectAsync(Guid id, RejectClaimRequest request, CancellationToken ct)
    {
        var claim = await LoadAsync(id, ct);
        if (claim is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), id);
        }

        if (claim.Version != request.Version)
        {
            return Error.ConcurrencyConflict;
        }

        var valid = claim.ValidateRejection();
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        var writtenOff = claim.Outstanding;
        if (!ClaimWriteOffs.NeedsApproval(writtenOff, await WriteOffApprovalAboveAsync(claim.FacilityId, ct)))
        {
            var rejected = await PostRejectionAsync(claim, request.Reason, null, ct);
            if (rejected.IsFailure)
            {
                return rejected.Error!;
            }

            await db.SaveChangesAsync(ct);
            return new ClaimWriteOffOutcome(null);
        }

        var asked = await AskWriteOffAsync(
            claim, new ClaimWriteOffPayload(claim.Id, ClaimWriteOffKind.Rejection, [], null, BillingTime.Today(time), request.Reason.Trim(), writtenOff),
            $"Reject claim {claim.ClaimNumber} and write off the ₹{Money.FormatInr(writtenOff)} the insurer still owes",
            [new ApprovalDetail("Insurer's reason", request.Reason.Trim())],
            request.Reason, ct);
        return asked.IsSuccess ? new ClaimWriteOffOutcome(asked.Value) : asked.Error!;
    }

    /// <summary>
    /// Posts a write-off someone else approved, in the approval's transaction (the approval saves): only over the claim as
    /// it was asked about (<c>approval.stale</c> when anything on it changed since).
    /// </summary>
    internal async Task<Result<string>> WriteOffApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ClaimWriteOffPayload>(request.Payload);
        var claim = await LoadAsync(asked.ClaimId, ct);
        if (claim is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), asked.ClaimId);
        }

        if (claim.Version != request.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"Claim {claim.ClaimNumber} changed after this was asked for (a settlement, a query or a rejection was recorded), so nothing was written off. Ask again, for the claim as it is now.");
        }

        var checker = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        var who = $"asked for by {request.RequestedByName}, approved by {checker}";
        if (asked.Kind == ClaimWriteOffKind.Deductions)
        {
            var bill = await db.Set<Bill>().FirstAsync(b => b.Id == claim.BillId, ct);
            var posted = await PostSettlementAsync(claim, bill, 0m, asked.Deductions, null, asked.Reference, asked.SettledOn, ct, who);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }
        }
        else
        {
            var rejected = await PostRejectionAsync(claim, asked.Reason ?? request.Reason, who, ct);
            if (rejected.IsFailure)
            {
                return rejected.Error!;
            }
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "insurance.claim.written_off",
            Fit($"{claim.ClaimNumber}: ₹{Money.FormatInr(asked.Amount)} written off ({(asked.Kind == ClaimWriteOffKind.Rejection ? "claim rejected" : "insurer's deductions")}; {who})", 1000))
        {
            PatientId = claim.PatientId,
            EntityType = nameof(InsuranceClaim),
            EntityId = claim.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { asked.Amount, kind = asked.Kind.ToString(), approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return $"₹{Money.FormatInr(asked.Amount)} written off on claim {claim.ClaimNumber}";
    }

    private async Task<Result<ApprovalRequestDto>> AskWriteOffAsync(
        InsuranceClaim claim, ClaimWriteOffPayload payload, string summary, List<ApprovalDetail> extra, string reason, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().Where(b => b.Id == claim.BillId).Select(b => new { b.BillNumber, b.PayerOutstanding }).FirstAsync(ct);
        var payer = await db.Set<Payer>().Where(p => p.Id == claim.PayerId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        var threshold = await WriteOffApprovalAboveAsync(claim.FacilityId, ct);
        List<ApprovalDetail> details =
        [
            new("Claim", $"{claim.ClaimNumber} on bill {bill.BillNumber}, {payer ?? "the payer"}"),
            new("Insurer owes", $"₹{Money.FormatInr(bill.PayerOutstanding)} → ₹{Money.FormatInr(Math.Max(bill.PayerOutstanding - payload.Amount, 0m))}: ₹{Money.FormatInr(payload.Amount)} is never collected from the insurer"),
            .. extra,
            new("Why it waits", threshold == 0m
                ? "Every insurance write-off is approved by a second person"
                : $"More than ₹{Money.FormatInr(threshold)}, the most the desk writes off alone"),
        ];
        return await approvals.RequestAsync(
            WriteOffKind,
            new ApprovalSubject(nameof(InsuranceClaim), claim.Id, claim.Version, claim.FacilityId, claim.PatientId),
            payload, Fit(summary, 300), details, reason, ct);
    }

    /// <summary>Records the settlement on the claim and the bill, the money as a payer receipt; the caller saves.</summary>
    private async Task<Result> PostSettlementAsync(
        InsuranceClaim claim, Bill bill, decimal amount, IReadOnlyList<DeductionInput> deductions, string? utrNumber, string? reference, DateOnly settledOn,
        CancellationToken ct, string? approvedBy = null)
    {
        var settled = claim.Settle(amount, deductions, utrNumber, reference, settledOn, time.GetUtcNow());
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

        if (settlement.Amount > 0)
        {
            var receipt = await receipts.RecordPayerSettlementAsync(bill, claim, settlement.Amount, settlement.UtrNumber, ct);
            settlement.ReceiptId = receipt.Id;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "insurance.claim.settled",
            $"{claim.ClaimNumber}: settled ₹{Money.FormatInr(settlement.Amount)}, deductions ₹{Money.FormatInr(settlement.DeductionAmount)}" + (approvedBy is null ? string.Empty : $" ({approvedBy})"))
        {
            PatientId = claim.PatientId,
            EntityType = nameof(InsuranceClaim),
            EntityId = claim.Id.ToString(),
        });
        return Result.Success();
    }

    /// <summary>Rejects the claim and disallows its unpaid balance on the bill; the caller saves.</summary>
    private async Task<Result> PostRejectionAsync(InsuranceClaim claim, string reason, string? approvedBy, CancellationToken ct)
    {
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

        audit.Add(new AuditEntry(AuditCategories.Data, "insurance.claim.rejected",
            $"{claim.ClaimNumber} rejected: {reason.Trim()}" + (approvedBy is null ? string.Empty : $" ({approvedBy})"))
        {
            PatientId = claim.PatientId,
            EntityType = nameof(InsuranceClaim),
            EntityId = claim.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        return Result.Success();
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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

    /// <summary>
    /// The coding medical records verified for a bill's stay or visit, as the text of a claim's codes; null until a second
    /// coder has verified it.
    /// </summary>
    public async Task<VerifiedClaimCoding?> VerifiedCodingAsync(
        Guid patientId, Guid? admissionId, Guid? visitId, CancellationToken ct) =>
        await verifiedCoding.FindAsync(patientId, admissionId, visitId, ct) is { } coding
            ? new VerifiedClaimCoding(
                VerifiedEpisodeCoding.AsText(coding.Diagnoses, InsuranceClaim.CodesMaxLength),
                VerifiedEpisodeCoding.AsText(coding.Procedures, InsuranceClaim.CodesMaxLength),
                coding.VerifiedAt)
            : null;

    /// <summary>
    /// The codes a new claim takes where the desk typed none: the coding medical records verified, procedures included
    /// (none coded means none done), or else the pre-authorisation's codes, which were provisional (its coded procedures,
    /// or the text typed before procedures were coded).
    /// </summary>
    internal static (string? Diagnoses, string? Procedures) InitialCoding(
        VerifiedClaimCoding? verified, PreAuthorization? preAuth) =>
        verified is not null
            ? (verified.DiagnosisCodes, verified.ProcedureCodes)
            : (preAuth?.DiagnosisCodes, preAuth?.ProcedureText(InsuranceClaim.CodesMaxLength));

    private Task<InsuranceClaim?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Set<InsuranceClaim>().Include(c => c.Settlements).Include(c => c.Deductions).FirstOrDefaultAsync(c => c.Id == id, ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum ClaimWriteOffKind
{
    /// <summary>The insurer's deductions on a settlement.</summary>
    Deductions = 0,

    /// <summary>The whole unpaid claim, on its rejection.</summary>
    Rejection,
}

/// <summary>A write-off as asked: the claim, what kind, the deductions (or the insurer's reason for rejecting) and the amount.</summary>
public sealed record ClaimWriteOffPayload(
    Guid ClaimId, ClaimWriteOffKind Kind, IReadOnlyList<DeductionInput> Deductions, string? Reference, DateOnly SettledOn, string? Reason, decimal Amount);

/// <summary>Posted at once (<paramref name="Asked"/> null), or the approval the write-off waits for.</summary>
public sealed record ClaimWriteOffOutcome(ApprovalRequestDto? Asked);

/// <summary>
/// An insurance write-off above the threshold (MAKER-CHECKER.md, slice 12): the claims desk asks
/// (<c>insurance.claims.manage</c>); someone else who approves insurance write-offs decides.
/// </summary>
internal sealed class ClaimWriteOffApproval(ClaimService claims) : IApprovalAction
{
    public string Kind => ClaimService.WriteOffKind;

    public string Title => "Insurance write-off";

    public string ApprovePermission => BillingPermissions.ClaimWriteOffApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => claims.WriteOffApprovedAsync(request, ct);
}
