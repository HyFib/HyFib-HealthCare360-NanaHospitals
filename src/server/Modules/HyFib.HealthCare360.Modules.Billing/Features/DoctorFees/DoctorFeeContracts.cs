using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;

public sealed record ShareRuleDto(
    Guid Id,
    Guid? PractitionerId,
    string? PractitionerName,
    Guid? ServiceItemId,
    string? ServiceCode,
    ServiceCategory? Category,
    PayerType? PayerType,
    ShareMethod Method,
    decimal Value,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive,
    string? Notes,
    uint Version);

/// <summary>A sharing rule; leave the practitioner empty for the hospital-wide default.</summary>
public sealed record SaveShareRuleRequest(
    Guid? PractitionerId,
    Guid? ServiceItemId,
    ServiceCategory? Category,
    PayerType? PayerType,
    ShareMethod Method,
    decimal Value,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive,
    string? Notes,
    uint? Version);

public sealed record AccrualDto(
    Guid Id,
    Guid PractitionerId,
    string? PractitionerName,
    Guid BillId,
    string BillNumber,
    string ServiceCode,
    string Description,
    ServiceCategory Category,
    PayerType PayerType,
    DateOnly ServiceDate,
    DateOnly AccruedOn,
    decimal BasisAmount,
    ShareMethod Method,
    decimal Rate,
    decimal ShareAmount,
    AccrualStatus Status,
    Guid? PayoutId,
    bool IsReversal);

public sealed record PayoutDeductionInput(string Description, decimal Amount);

/// <summary>Closes the period for the doctor's unpaid accruals; TDS defaults to the facility setting (10% under 194J).</summary>
public sealed record CreatePayoutRequest(
    Guid PractitionerId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    IReadOnlyList<PayoutDeductionInput>? Deductions,
    decimal? TdsPercent,
    string? Notes);

public sealed record PayoutDeductionDto(Guid Id, string Description, decimal Amount);

public sealed record PayoutDto(
    Guid Id,
    string PayoutNumber,
    Guid PractitionerId,
    string? PractitionerName,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    decimal GrossAmount,
    decimal DeductionAmount,
    decimal TdsPercent,
    decimal TdsAmount,
    decimal NetAmount,
    PayoutStatus Status,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? PaidAt,
    string? PaidReference,
    PaymentMode? PaymentMode,
    string? Notes,
    int AccrualCount,
    IReadOnlyList<PayoutDeductionDto> Deductions,
    uint Version);

public sealed record PayoutVersionRequest(uint Version);

public sealed class PayoutVersionRequestValidator : AbstractValidator<PayoutVersionRequest>
{
    public PayoutVersionRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed record PayPayoutRequest(PaymentMode Mode, string? Reference, uint Version);

/// <summary>Doctor fee statement (DFA-04) for a period.</summary>
public sealed record DoctorStatement(
    Guid PractitionerId,
    string? PractitionerName,
    DateOnly From,
    DateOnly To,
    decimal Accrued,
    decimal Reversed,
    decimal InPayout,
    decimal PaidOut,
    decimal Unpaid,
    IReadOnlyList<AccrualDto> Accruals,
    IReadOnlyList<PayoutDto> Payouts);

public sealed class SaveShareRuleRequestValidator : AbstractValidator<SaveShareRuleRequest>
{
    public SaveShareRuleRequestValidator()
    {
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Value).Percent().When(x => x.Method == ShareMethod.Percentage);
        RuleFor(x => x.Value).NonNegativeAmount().When(x => x.Method == ShareMethod.FixedPerUnit);
        RuleFor(x => x.EffectiveTo)
            .Must((request, to) => to is null || to >= request.EffectiveFrom)
            .WithMessage("The end date must be on or after the start date.");
        RuleFor(x => x.Notes).SafeText(300);
    }
}

public sealed class CreatePayoutRequestValidator : AbstractValidator<CreatePayoutRequest>
{
    public CreatePayoutRequestValidator()
    {
        RuleFor(x => x.PractitionerId).NotEmpty();
        RuleFor(x => x.PeriodTo).GreaterThanOrEqualTo(x => x.PeriodFrom).WithMessage("The period must end on or after its start.");
        RuleFor(x => x.TdsPercent).OptionalPercent();
        RuleFor(x => x.Deductions).Must(d => d is null || d.Count <= 20).WithMessage("Record at most 20 deductions.");
        RuleForEach(x => x.Deductions).ChildRules(d =>
        {
            d.RuleFor(x => x.Description).NotEmpty().SafeText(200);
            d.RuleFor(x => x.Amount).Amount();
        });
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class PayPayoutRequestValidator : AbstractValidator<PayPayoutRequest>
{
    public PayPayoutRequestValidator()
    {
        RuleFor(x => x.Mode).IsInEnum().Must(m => m is not (PaymentMode.Advance or PaymentMode.Insurance)).WithMessage("Choose how the doctor is paid.");
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}
