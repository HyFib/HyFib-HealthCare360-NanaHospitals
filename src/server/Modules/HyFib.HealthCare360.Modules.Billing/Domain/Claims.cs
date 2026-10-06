namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum ClaimStatus
{
    Submitted = 0,
    Query,
    Approved,
    Settled,
    PartiallySettled,
    Rejected,
}

public sealed record DeductionInput(string Reason, decimal Amount);

/// <summary>
/// A claim on an insurer/TPA/corporate for the payer share of a finalized bill (INS-05/06): documents, coding, queries,
/// approval, one or more settlements (UTR) and itemised deductions. Settlements and deductions flow back to the bill's
/// payer balance, which drives receivable ageing (INS-07).
/// </summary>
public sealed class InsuranceClaim : AuditableEntity, IFacilityScoped
{
    /// <summary>Longest text <see cref="DiagnosisCodes"/> and <see cref="ProcedureCodes"/> each hold.</summary>
    public const int CodesMaxLength = 1000;

    public Guid FacilityId { get; set; }
    public string ClaimNumber { get; set; } = string.Empty;
    public Guid BillId { get; set; }
    public Guid PatientId { get; set; }
    public Guid PayerId { get; set; }
    public Guid? TpaId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? PreAuthorizationId { get; set; }
    public decimal ClaimedAmount { get; set; }

    /// <summary>ICD-10 diagnosis codes with descriptions (MB-03).</summary>
    public string? DiagnosisCodes { get; set; }

    /// <summary>ICD-10-PCS / procedure codes with descriptions (MB-03).</summary>
    public string? ProcedureCodes { get; set; }

    public List<Guid> DocumentFileIds { get; set; } = [];
    public ClaimStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Claim id issued by the insurer/TPA portal.</summary>
    public string? PayerClaimReference { get; set; }

    public string? QueryText { get; set; }
    public DateTimeOffset? QueryRaisedAt { get; set; }
    public string? QueryResponse { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public decimal SettledAmount { get; set; }
    public decimal DeductionAmount { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public string? UtrNumber { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public List<ClaimSettlement> Settlements { get; set; } = [];
    public List<ClaimDeduction> Deductions { get; set; } = [];

    public decimal Outstanding => Math.Max(ClaimedAmount - SettledAmount - DeductionAmount, 0m);

    public bool IsClosed => Status is ClaimStatus.Settled or ClaimStatus.Rejected;

    public void UpdateCoding(string? diagnosisCodes, string? procedureCodes)
    {
        DiagnosisCodes = Clean(diagnosisCodes);
        ProcedureCodes = Clean(procedureCodes);
    }

    public Result RaiseQuery(string query, DateTimeOffset now)
    {
        if (Status is not (ClaimStatus.Submitted or ClaimStatus.Approved))
        {
            return Error.Rule("insurance.claim_not_open", "Queries can be recorded only on submitted or approved claims.");
        }

        Status = ClaimStatus.Query;
        QueryText = query.Trim();
        QueryRaisedAt = now;
        return Result.Success();
    }

    public Result AnswerQuery(string response, DateTimeOffset now)
    {
        if (Status != ClaimStatus.Query)
        {
            return Error.Rule("insurance.claim_no_query", "There is no open query on this claim.");
        }

        Status = ApprovedAmount is null ? ClaimStatus.Submitted : ClaimStatus.Approved;
        QueryResponse = response.Trim();
        RespondedAt = now;
        return Result.Success();
    }

    public Result Approve(decimal amount, DateTimeOffset now)
    {
        if (Status is not (ClaimStatus.Submitted or ClaimStatus.Query))
        {
            return Error.Rule("insurance.claim_not_pending", "Only submitted claims can be approved.");
        }

        if (amount <= 0 || amount > ClaimedAmount)
        {
            return Error.Field("approvedAmount", $"The approved amount must be between ₹1 and the claimed ₹{Money.FormatInr(ClaimedAmount)}.");
        }

        ApprovedAmount = Money.Round(amount);
        ApprovedAt = now;
        Status = ClaimStatus.Approved;
        return Result.Success();
    }

    /// <summary>
    /// Records a settlement (the payer may pay in tranches). Settled + deductions can never exceed the claimed amount;
    /// the claim is Settled when nothing remains outstanding, otherwise PartiallySettled.
    /// </summary>
    public Result<ClaimSettlement> Settle(decimal amount, IReadOnlyList<DeductionInput> deductions, string? utrNumber, string? reference, DateOnly settledOn, DateTimeOffset now)
    {
        if (Status is not (ClaimStatus.Submitted or ClaimStatus.Approved or ClaimStatus.PartiallySettled))
        {
            return Error.Rule("insurance.claim_not_settleable", "Answer open queries before recording a settlement; settled or rejected claims are closed.");
        }

        if (amount < 0 || deductions.Any(d => d.Amount <= 0 || string.IsNullOrWhiteSpace(d.Reason)))
        {
            return Error.Field("deductions", "Every deduction needs a reason and an amount greater than zero.");
        }

        var deducted = deductions.Sum(d => Money.Round(d.Amount));
        var total = Money.Round(amount) + deducted;
        if (total <= 0)
        {
            return Error.Field("amount", "Enter the settled amount or the deductions.");
        }

        if (total > Outstanding)
        {
            return Error.Rule("insurance.settlement_exceeds", $"Only ₹{Money.FormatInr(Outstanding)} is outstanding on this claim.");
        }

        if (amount > 0 && string.IsNullOrWhiteSpace(utrNumber))
        {
            return Error.Field("utrNumber", "Enter the UTR of the bank transfer.");
        }

        var settlement = new ClaimSettlement
        {
            ClaimId = Id,
            Amount = Money.Round(amount),
            DeductionAmount = deducted,
            UtrNumber = Clean(utrNumber),
            Reference = Clean(reference),
            SettledOn = settledOn,
            RecordedAt = now,
        };
        Settlements.Add(settlement);
        foreach (var deduction in deductions)
        {
            Deductions.Add(new ClaimDeduction { ClaimId = Id, SettlementId = settlement.Id, Reason = deduction.Reason.Trim(), Amount = Money.Round(deduction.Amount) });
        }

        SettledAmount += settlement.Amount;
        DeductionAmount += deducted;
        SettledAt = now;
        UtrNumber = settlement.UtrNumber ?? UtrNumber;
        Status = Outstanding == 0 ? ClaimStatus.Settled : ClaimStatus.PartiallySettled;
        return settlement;
    }

    /// <summary>
    /// Rejects the claim. The unpaid balance is recorded as a full deduction (a zero-amount settlement), so the bill's
    /// payer share, the receivable ageing and the Tally disallowance journal all show the loss on the same day.
    /// </summary>
    public Result<ClaimSettlement?> Reject(string reason, DateOnly rejectedOn, DateTimeOffset now)
    {
        if (Status is ClaimStatus.Settled or ClaimStatus.PartiallySettled or ClaimStatus.Rejected)
        {
            return Error.Rule("insurance.claim_not_rejectable", "Claims with settlements, or already rejected, cannot be rejected.");
        }

        var outstanding = Outstanding;
        Status = ClaimStatus.Rejected;
        RejectionReason = reason.Trim();
        RejectedAt = now;
        if (outstanding <= 0)
        {
            return Result<ClaimSettlement?>.Ok(null);
        }

        var settlement = new ClaimSettlement
        {
            ClaimId = Id,
            Amount = 0m,
            DeductionAmount = outstanding,
            Reference = "Claim rejected",
            SettledOn = rejectedOn,
            RecordedAt = now,
        };
        Settlements.Add(settlement);
        Deductions.Add(new ClaimDeduction { ClaimId = Id, SettlementId = settlement.Id, Reason = $"Claim rejected: {RejectionReason}", Amount = outstanding });
        DeductionAmount += outstanding;
        return settlement;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ClaimSettlement : Entity
{
    public Guid ClaimId { get; set; }
    public decimal Amount { get; set; }
    public decimal DeductionAmount { get; set; }
    public string? UtrNumber { get; set; }
    public string? Reference { get; set; }
    public DateOnly SettledOn { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>Payer-settlement receipt recorded for the money (none when the tranche is only deductions).</summary>
    public Guid? ReceiptId { get; set; }
}

public sealed class ClaimDeduction : Entity
{
    public Guid ClaimId { get; set; }
    public Guid? SettlementId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
