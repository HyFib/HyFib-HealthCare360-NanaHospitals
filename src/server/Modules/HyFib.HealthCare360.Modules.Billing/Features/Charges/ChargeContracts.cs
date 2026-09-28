using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Charges;

public sealed record ChargeDto(
    Guid Id,
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid ServiceItemId,
    string ServiceCode,
    string Description,
    ServiceCategory Category,
    decimal Quantity,
    decimal UnitPrice,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal NetAmount,
    PriceSource PriceSource,
    string SourceModule,
    string SourceReference,
    Guid? PractitionerId,
    string? PractitionerName,
    Guid? DepartmentId,
    DateTimeOffset ServiceDate,
    ChargeStatus Status,
    Guid? BillId,
    string? CancelReason);

/// <summary>A service charged manually by the billing desk (for example a procedure not ordered electronically).</summary>
public sealed record PostChargeRequest(
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid ServiceItemId,
    decimal Quantity,
    decimal? UnitPrice,
    string? Description,
    Guid? PractitionerId,
    Guid? DepartmentId,
    DateTimeOffset? ServiceDate);

public sealed record CancelChargeRequest(string Reason);

public sealed class PostChargeRequestValidator : AbstractValidator<PostChargeRequest>
{
    public PostChargeRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ServiceItemId).NotEmpty();
        RuleFor(x => x.Quantity).Quantity();
        RuleFor(x => x.UnitPrice).OptionalAmount();
        RuleFor(x => x.Description).SafeText(300);
        RuleFor(x => x.ServiceDate)
            .Must(date => date is null || date <= time.GetUtcNow().AddHours(1))
            .WithMessage("Services cannot be charged for a future date.");
    }
}

public sealed class CancelChargeRequestValidator : AbstractValidator<CancelChargeRequest>
{
    public CancelChargeRequestValidator() => RuleFor(x => x.Reason).Reason();
}
