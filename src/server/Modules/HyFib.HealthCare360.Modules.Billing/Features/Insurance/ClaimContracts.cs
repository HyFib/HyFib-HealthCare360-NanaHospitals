using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Insurance;

public sealed record ClaimListItem(
    Guid Id,
    string ClaimNumber,
    Guid BillId,
    string? BillNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid PayerId,
    string? PayerName,
    decimal ClaimedAmount,
    decimal? ApprovedAmount,
    decimal SettledAmount,
    decimal DeductionAmount,
    ClaimStatus Status,
    DateTimeOffset SubmittedAt);

public sealed record ClaimSettlementDto(Guid Id, decimal Amount, decimal DeductionAmount, string? UtrNumber, string? Reference, DateOnly SettledOn, Guid? ReceiptId);

public sealed record ClaimDeductionDto(Guid Id, Guid? SettlementId, string Reason, decimal Amount);

public sealed record ClaimDetail(
    Guid Id,
    string ClaimNumber,
    Guid BillId,
    string? BillNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid PayerId,
    string? PayerName,
    Guid? TpaId,
    Guid? PolicyId,
    Guid? PreAuthorizationId,
    decimal ClaimedAmount,
    string? DiagnosisCodes,
    string? ProcedureCodes,
    IReadOnlyList<Guid> DocumentFileIds,
    ClaimStatus Status,
    DateTimeOffset SubmittedAt,
    string? PayerClaimReference,
    string? QueryText,
    string? QueryResponse,
    decimal? ApprovedAmount,
    decimal SettledAmount,
    decimal DeductionAmount,
    decimal Outstanding,
    string? UtrNumber,
    string? RejectionReason,
    IReadOnlyList<ClaimSettlementDto> Settlements,
    IReadOnlyList<ClaimDeductionDto> Deductions,
    uint Version);

/// <summary>Claim on the payer share of a finalized bill; codes default to those of the pre-authorisation.</summary>
public sealed record CreateClaimRequest(
    Guid BillId,
    decimal? ClaimedAmount,
    string? DiagnosisCodes,
    string? ProcedureCodes,
    IReadOnlyList<Guid>? DocumentFileIds,
    string? PayerClaimReference);

/// <summary>Claim coding (MB-03): ICD-10 diagnoses and ICD-10-PCS procedures as text, documents and the payer's reference.</summary>
public sealed record UpdateClaimCodingRequest(
    string? DiagnosisCodes,
    string? ProcedureCodes,
    IReadOnlyList<Guid>? DocumentFileIds,
    string? PayerClaimReference,
    uint Version);

public sealed record ClaimTextRequest(string Text);

public sealed record ApproveClaimRequest(decimal ApprovedAmount);

public sealed record SettleClaimRequest(decimal Amount, IReadOnlyList<DeductionInput>? Deductions, string? UtrNumber, string? Reference, DateOnly? SettledOn);

public sealed record RejectClaimRequest(string Reason);

public sealed record AgeingRow(
    Guid PayerId,
    string PayerName,
    PayerCategory? Category,
    int CreditDays,
    decimal Days0To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90,
    decimal Total);

/// <summary>Receivable ageing by payer (INS-07) as of a date.</summary>
public sealed record ReceivablesAgeingReport(
    DateOnly AsOf,
    IReadOnlyList<AgeingRow> Payers,
    decimal Days0To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90,
    decimal Total);

public sealed class CreateClaimRequestValidator : AbstractValidator<CreateClaimRequest>
{
    public CreateClaimRequestValidator()
    {
        RuleFor(x => x.BillId).NotEmpty();
        RuleFor(x => x.ClaimedAmount).OptionalAmount();
        RuleFor(x => x.DiagnosisCodes).SafeText(1000);
        RuleFor(x => x.ProcedureCodes).SafeText(1000);
        RuleFor(x => x.DocumentFileIds).Must(ids => ids is null || ids.Count <= 50).WithMessage("Attach at most 50 documents.");
        RuleFor(x => x.PayerClaimReference).MaximumLength(64);
    }
}

public sealed class UpdateClaimCodingRequestValidator : AbstractValidator<UpdateClaimCodingRequest>
{
    public UpdateClaimCodingRequestValidator()
    {
        RuleFor(x => x.DiagnosisCodes).SafeText(1000);
        RuleFor(x => x.ProcedureCodes).SafeText(1000);
        RuleFor(x => x.DocumentFileIds).Must(ids => ids is null || ids.Count <= 50).WithMessage("Attach at most 50 documents.");
        RuleFor(x => x.PayerClaimReference).MaximumLength(64);
    }
}

public sealed class ClaimTextRequestValidator : AbstractValidator<ClaimTextRequest>
{
    public ClaimTextRequestValidator() => RuleFor(x => x.Text).NotEmpty().SafeText(2000);
}

public sealed class ApproveClaimRequestValidator : AbstractValidator<ApproveClaimRequest>
{
    public ApproveClaimRequestValidator() => RuleFor(x => x.ApprovedAmount).Amount();
}

public sealed class SettleClaimRequestValidator : AbstractValidator<SettleClaimRequest>
{
    public SettleClaimRequestValidator()
    {
        RuleFor(x => x.Amount).NonNegativeAmount();
        RuleFor(x => x.UtrNumber).MaximumLength(64);
        RuleFor(x => x.Reference).MaximumLength(64);
        RuleFor(x => x.Deductions).Must(d => d is null || d.Count <= 50).WithMessage("Record at most 50 deductions per settlement.");
        RuleForEach(x => x.Deductions).ChildRules(d =>
        {
            d.RuleFor(x => x.Reason).NotEmpty().SafeText(300);
            d.RuleFor(x => x.Amount).Amount();
        });
    }
}

public sealed class RejectClaimRequestValidator : AbstractValidator<RejectClaimRequest>
{
    public RejectClaimRequestValidator() => RuleFor(x => x.Reason).Reason(1000);
}
