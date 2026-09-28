namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum PreAuthStatus
{
    Draft = 0,
    Submitted,

    /// <summary>The insurer/TPA asked for more information.</summary>
    Query,

    Approved,
    PartiallyApproved,
    Rejected,
}

public enum EnhancementStatus
{
    Pending = 0,
    Approved,
    Rejected,
}

/// <summary>
/// Cashless pre-authorisation (INS-04): initial request, queries, approval, enhancements when the stay costs more than
/// estimated, and the final approval at discharge. The effective approved amount caps the payer share of the IP bill.
/// </summary>
public sealed class PreAuthorization : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid PolicyId { get; set; }
    public Guid PayerId { get; set; }
    public Guid? TpaId { get; set; }
    public decimal RequestedAmount { get; set; }

    /// <summary>ICD-10 diagnosis codes with descriptions, as entered for the claim (MB-03).</summary>
    public string? DiagnosisCodes { get; set; }

    /// <summary>ICD-10-PCS / procedure codes with descriptions.</summary>
    public string? ProcedureCodes { get; set; }

    public string? ClinicalSummary { get; set; }
    public int? ExpectedStayDays { get; set; }

    /// <summary>Supporting documents in the encrypted file store.</summary>
    public List<Guid> DocumentFileIds { get; set; } = [];

    public PreAuthStatus Status { get; set; }
    public decimal ApprovedAmount { get; set; }
    public string? ApprovalReference { get; set; }
    public decimal? FinalApprovedAmount { get; set; }
    public string? QueryText { get; set; }
    public string? QueryResponse { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? QueryRaisedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public DateTimeOffset? FinalApprovedAt { get; set; }
    public List<PreAuthEnhancement> Enhancements { get; set; } = [];

    public bool IsApproved => Status is PreAuthStatus.Approved or PreAuthStatus.PartiallyApproved;

    /// <summary>The final approval once given; otherwise the initial approval plus approved enhancements.</summary>
    public decimal EffectiveApprovedAmount => FinalApprovedAmount
        ?? ApprovedAmount + Enhancements.Where(e => e.Status == EnhancementStatus.Approved).Sum(e => e.ApprovedAmount);

    public Result Submit(DateTimeOffset now)
    {
        if (Status != PreAuthStatus.Draft)
        {
            return Error.Rule("insurance.preauth_not_draft", "Only draft pre-authorisations can be submitted.");
        }

        if (RequestedAmount <= 0)
        {
            return Error.Rule("insurance.preauth_amount", "Enter the estimated amount before submitting.");
        }

        Status = PreAuthStatus.Submitted;
        SubmittedAt = now;
        return Result.Success();
    }

    public Result RaiseQuery(string query, DateTimeOffset now)
    {
        if (Status != PreAuthStatus.Submitted)
        {
            return Error.Rule("insurance.preauth_not_submitted", "Queries can be recorded only on submitted pre-authorisations.");
        }

        Status = PreAuthStatus.Query;
        QueryText = query.Trim();
        QueryRaisedAt = now;
        return Result.Success();
    }

    public Result AnswerQuery(string response, DateTimeOffset now)
    {
        if (Status != PreAuthStatus.Query)
        {
            return Error.Rule("insurance.preauth_no_query", "There is no open query on this pre-authorisation.");
        }

        Status = PreAuthStatus.Submitted;
        QueryResponse = response.Trim();
        SubmittedAt = now;
        return Result.Success();
    }

    public Result Approve(decimal amount, string? reference, DateTimeOffset now)
    {
        if (Status is not (PreAuthStatus.Submitted or PreAuthStatus.Query))
        {
            return Error.Rule("insurance.preauth_not_pending", "Only submitted pre-authorisations can be approved.");
        }

        if (amount <= 0)
        {
            return Error.Field("approvedAmount", "Enter the approved amount.");
        }

        ApprovedAmount = Money.Round(amount);
        ApprovalReference = Clean(reference);
        Status = ApprovedAmount >= RequestedAmount ? PreAuthStatus.Approved : PreAuthStatus.PartiallyApproved;
        ApprovedAt = now;
        return Result.Success();
    }

    public Result Reject(string reason, DateTimeOffset now)
    {
        if (Status is not (PreAuthStatus.Submitted or PreAuthStatus.Query))
        {
            return Error.Rule("insurance.preauth_not_pending", "Only submitted pre-authorisations can be rejected.");
        }

        Status = PreAuthStatus.Rejected;
        RejectionReason = reason.Trim();
        RejectedAt = now;
        return Result.Success();
    }

    public Result<PreAuthEnhancement> RequestEnhancement(decimal amount, string reason, DateTimeOffset now)
    {
        if (!IsApproved)
        {
            return Error.Rule("insurance.preauth_not_approved", "Enhancements can be requested only after approval.");
        }

        if (FinalApprovedAmount is not null)
        {
            return Error.Rule("insurance.preauth_final", "The final approval has already been recorded.");
        }

        if (Enhancements.Any(e => e.Status == EnhancementStatus.Pending))
        {
            return Error.Conflict("insurance.enhancement_pending", "An enhancement request is already awaiting the insurer's reply.");
        }

        if (amount <= 0)
        {
            return Error.Field("amount", "Enter the additional amount requested.");
        }

        var enhancement = new PreAuthEnhancement
        {
            PreAuthorizationId = Id,
            RequestedAmount = Money.Round(amount),
            Reason = reason.Trim(),
            Status = EnhancementStatus.Pending,
            RequestedAt = now,
        };
        Enhancements.Add(enhancement);
        return enhancement;
    }

    public Result RespondToEnhancement(Guid enhancementId, bool approved, decimal approvedAmount, string? reference, DateTimeOffset now)
    {
        var enhancement = Enhancements.FirstOrDefault(e => e.Id == enhancementId);
        if (enhancement is null)
        {
            return Error.NotFound(nameof(PreAuthEnhancement), enhancementId);
        }

        if (enhancement.Status != EnhancementStatus.Pending)
        {
            return Error.Rule("insurance.enhancement_decided", "This enhancement has already been answered.");
        }

        if (approved && approvedAmount <= 0)
        {
            return Error.Field("approvedAmount", "Enter the approved enhancement amount.");
        }

        enhancement.Status = approved ? EnhancementStatus.Approved : EnhancementStatus.Rejected;
        enhancement.ApprovedAmount = approved ? Money.Round(approvedAmount) : 0m;
        enhancement.Reference = Clean(reference);
        enhancement.RespondedAt = now;
        return Result.Success();
    }

    /// <summary>The insurer's final approval against the final bill (it replaces the running approvals).</summary>
    public Result RecordFinalApproval(decimal amount, string? reference, DateTimeOffset now)
    {
        if (!IsApproved)
        {
            return Error.Rule("insurance.preauth_not_approved", "Record the initial approval first.");
        }

        if (amount < 0)
        {
            return Error.Field("amount", "Enter the final approved amount.");
        }

        FinalApprovedAmount = Money.Round(amount);
        ApprovalReference = Clean(reference) ?? ApprovalReference;
        FinalApprovedAt = now;
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class PreAuthEnhancement : Entity
{
    public Guid PreAuthorizationId { get; set; }
    public decimal RequestedAmount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public EnhancementStatus Status { get; set; }
    public decimal ApprovedAmount { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}
