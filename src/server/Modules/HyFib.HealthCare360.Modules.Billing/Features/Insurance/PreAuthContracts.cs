using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Insurance;

public sealed record PreAuthListItem(
    Guid Id,
    string Number,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? AdmissionId,
    Guid PayerId,
    string? PayerName,
    decimal RequestedAmount,
    decimal EffectiveApprovedAmount,
    PreAuthStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt);

public sealed record EnhancementDto(
    Guid Id,
    decimal RequestedAmount,
    string Reason,
    EnhancementStatus Status,
    decimal ApprovedAmount,
    string? Reference,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RespondedAt);

public sealed record PreAuthDetail(
    Guid Id,
    string Number,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? AdmissionId,
    Guid? VisitId,
    Guid PolicyId,
    string? PolicyNumberMasked,
    Guid PayerId,
    string? PayerName,
    Guid? TpaId,
    string? TpaName,
    decimal RequestedAmount,
    string? DiagnosisCodes,
    string? ProcedureCodes,
    string? ClinicalSummary,
    int? ExpectedStayDays,
    IReadOnlyList<Guid> DocumentFileIds,
    PreAuthStatus Status,
    decimal ApprovedAmount,
    string? ApprovalReference,
    decimal? FinalApprovedAmount,
    decimal EffectiveApprovedAmount,
    string? QueryText,
    string? QueryResponse,
    string? RejectionReason,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? RejectedAt,
    IReadOnlyList<EnhancementDto> Enhancements,
    uint Version);

public sealed record SavePreAuthRequest(
    Guid PolicyId,
    Guid? AdmissionId,
    Guid? VisitId,
    decimal RequestedAmount,
    string? DiagnosisCodes,
    string? ProcedureCodes,
    string? ClinicalSummary,
    int? ExpectedStayDays,
    IReadOnlyList<Guid>? DocumentFileIds,
    uint? Version);

public sealed record PreAuthTextRequest(string Text);

public sealed record ApprovePreAuthRequest(decimal ApprovedAmount, string? Reference);

public sealed record RejectPreAuthRequest(string Reason);

public sealed record RequestEnhancementRequest(decimal Amount, string Reason);

public sealed record RespondEnhancementRequest(bool Approved, decimal ApprovedAmount, string? Reference);

public sealed record FinalApprovalRequest(decimal Amount, string? Reference);

public sealed class SavePreAuthRequestValidator : AbstractValidator<SavePreAuthRequest>
{
    public SavePreAuthRequestValidator()
    {
        RuleFor(x => x.PolicyId).NotEmpty();
        RuleFor(x => x.RequestedAmount).NonNegativeAmount();
        RuleFor(x => x.DiagnosisCodes).SafeText(1000);
        RuleFor(x => x.ProcedureCodes).SafeText(1000);
        RuleFor(x => x.ClinicalSummary).SafeText(4000);
        RuleFor(x => x.ExpectedStayDays).InclusiveBetween(1, 365);
        RuleFor(x => x.DocumentFileIds).Must(ids => ids is null || ids.Count <= 50).WithMessage("Attach at most 50 documents.");
    }
}

public sealed class PreAuthTextRequestValidator : AbstractValidator<PreAuthTextRequest>
{
    public PreAuthTextRequestValidator() => RuleFor(x => x.Text).NotEmpty().SafeText(2000);
}

public sealed class ApprovePreAuthRequestValidator : AbstractValidator<ApprovePreAuthRequest>
{
    public ApprovePreAuthRequestValidator()
    {
        RuleFor(x => x.ApprovedAmount).Amount();
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}

public sealed class RejectPreAuthRequestValidator : AbstractValidator<RejectPreAuthRequest>
{
    public RejectPreAuthRequestValidator() => RuleFor(x => x.Reason).Reason(1000);
}

public sealed class RequestEnhancementRequestValidator : AbstractValidator<RequestEnhancementRequest>
{
    public RequestEnhancementRequestValidator()
    {
        RuleFor(x => x.Amount).Amount();
        RuleFor(x => x.Reason).Reason(500);
    }
}

public sealed class RespondEnhancementRequestValidator : AbstractValidator<RespondEnhancementRequest>
{
    public RespondEnhancementRequestValidator()
    {
        RuleFor(x => x.ApprovedAmount).NonNegativeAmount();
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}

public sealed class FinalApprovalRequestValidator : AbstractValidator<FinalApprovalRequest>
{
    public FinalApprovalRequestValidator()
    {
        RuleFor(x => x.Amount).NonNegativeAmount();
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}
