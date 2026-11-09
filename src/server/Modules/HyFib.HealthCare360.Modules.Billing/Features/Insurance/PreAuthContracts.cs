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
    IReadOnlyList<PreAuthProcedureDto> Procedures,
    string? LegacyProcedureText,
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
    uint Version,
    DateTimeOffset? AmountAskedAt = null);

public sealed record SavePreAuthRequest(
    Guid PolicyId,
    Guid? AdmissionId,
    Guid? VisitId,
    decimal RequestedAmount,
    string? DiagnosisCodes,
    IReadOnlyList<PreAuthProcedureLine>? Procedures,
    string? ClinicalSummary,
    int? ExpectedStayDays,
    IReadOnlyList<Guid>? DocumentFileIds,
    uint? Version);

/// <summary>A planned procedure as the desk codes it: the system and code, how many times, and the amount asked for it.</summary>
public sealed record PreAuthProcedureLine(PreAuthProcedureSystem System, string Code, int Quantity, decimal Amount);

/// <summary>A planned procedure as saved: the code with the name the master gave it.</summary>
public sealed record PreAuthProcedureDto(PreAuthProcedureSystem System, string Code, string Name, int Quantity, decimal Amount);

/// <summary>A procedure the desk may code: from the HBP master (with the scheme's base rate) or the ICD-10-PCS catalogue.</summary>
public sealed record PreAuthProcedureOption(PreAuthProcedureSystem System, string Code, string Name, decimal? Rate);

public sealed record PreAuthTextRequest(string Text);

/// <param name="Version">The pre-authorisation's version the desk saw.</param>
public sealed record ApprovePreAuthRequest(decimal ApprovedAmount, string? Reference, uint Version);

public sealed record RejectPreAuthRequest(string Reason);

public sealed record RequestEnhancementRequest(decimal Amount, string Reason);

public sealed record RespondEnhancementRequest(bool Approved, decimal ApprovedAmount, string? Reference, uint Version);

public sealed record FinalApprovalRequest(decimal Amount, string? Reference, uint Version);

public sealed class SavePreAuthRequestValidator : AbstractValidator<SavePreAuthRequest>
{
    public SavePreAuthRequestValidator()
    {
        RuleFor(x => x.PolicyId).NotEmpty();
        RuleFor(x => x.RequestedAmount).NonNegativeAmount();
        RuleFor(x => x.DiagnosisCodes).SafeText(1000);
        RuleFor(x => x.Procedures).Must(lines => lines is null || lines.Count <= PreAuthProcedure.MaxLines)
            .WithMessage($"Plan at most {PreAuthProcedure.MaxLines} procedures.")
            .Must(lines => lines is null || lines.DistinctBy(l => (l.System, (l.Code ?? string.Empty).Trim().ToUpperInvariant())).Count() == lines.Count)
            .WithMessage("Each procedure is listed once: raise its quantity instead.");
        RuleForEach(x => x.Procedures).ChildRules(line =>
        {
            line.RuleFor(l => l.System).IsInEnum();
            line.RuleFor(l => l.Code).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9]+$").WithMessage("Enter the procedure's code.");
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, PreAuthProcedure.MaxQuantity);
            line.RuleFor(l => l.Amount).NonNegativeAmount();
        });
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
        RuleFor(x => x.Version).SeenVersion();
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
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class FinalApprovalRequestValidator : AbstractValidator<FinalApprovalRequest>
{
    public FinalApprovalRequestValidator()
    {
        RuleFor(x => x.Amount).NonNegativeAmount();
        RuleFor(x => x.Reference).MaximumLength(64);
        RuleFor(x => x.Version).SeenVersion();
    }
}
