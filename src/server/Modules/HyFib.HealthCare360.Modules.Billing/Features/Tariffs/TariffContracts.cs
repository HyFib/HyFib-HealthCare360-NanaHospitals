using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tariffs;

public sealed record TariffPlanListItem(
    Guid Id,
    string Code,
    string Name,
    TariffType Type,
    Guid? PayerId,
    string? PayerName,
    Guid? FacilityId,
    decimal DefaultDiscountPercent,
    bool IsDefault,
    bool IsActive,
    int PriceCount);

public sealed record BedMultiplierDto(Guid BedServiceItemId, string? BedServiceCode, string? BedServiceName, decimal Multiplier);

public sealed record TariffPlanDetail(
    Guid Id,
    string Code,
    string Name,
    TariffType Type,
    Guid? PayerId,
    Guid? FacilityId,
    decimal DefaultDiscountPercent,
    bool IsDefault,
    bool IsActive,
    string? Description,
    IReadOnlyList<BedMultiplierDto> BedMultipliers,
    uint Version);

public sealed record BedMultiplierInput(Guid BedServiceItemId, decimal Multiplier);

/// <summary>Create (no <paramref name="Version"/>) or update (with the last-seen version) a tariff plan.</summary>
public sealed record SaveTariffPlanRequest(
    string Code,
    string Name,
    TariffType Type,
    Guid? PayerId,
    Guid? FacilityId,
    decimal DefaultDiscountPercent,
    bool IsDefault,
    bool IsActive,
    string? Description,
    IReadOnlyList<BedMultiplierInput>? BedMultipliers,
    uint? Version);

public sealed record TariffPriceDto(
    Guid Id,
    Guid ServiceItemId,
    string ServiceCode,
    string ServiceName,
    ServiceCategory Category,
    decimal BasePrice,
    decimal Price,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    uint Version);

/// <param name="Version">The version of the price being changed (REL-04); none for a new start date, and none in a bulk import.</param>
public sealed record SaveTariffPriceRequest(Guid ServiceItemId, decimal Price, DateOnly EffectiveFrom, DateOnly? EffectiveTo, uint? Version = null);

public sealed record BulkTariffPricesRequest(IReadOnlyList<SaveTariffPriceRequest> Prices);

public sealed record BulkTariffPricesResult(int Created, int Updated);

public sealed record PriceQuoteDto(
    Guid ServiceItemId,
    string ServiceCode,
    string ServiceName,
    ServiceCategory Category,
    decimal UnitPrice,
    PriceSource Source,
    Guid? TariffPlanId,
    string? TariffPlanName,
    decimal Multiplier,
    decimal TaxRate,
    bool IsPriceEditable,
    bool IsDiscountAllowed);

public sealed class SaveTariffPlanRequestValidator : AbstractValidator<SaveTariffPlanRequest>
{
    public SaveTariffPlanRequestValidator()
    {
        RuleFor(x => x.Code).Code();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.DefaultDiscountPercent).Percent();
        RuleFor(x => x.Description).SafeText(500);
        RuleForEach(x => x.BedMultipliers).ChildRules(m =>
        {
            m.RuleFor(x => x.BedServiceItemId).NotEmpty();
            m.RuleFor(x => x.Multiplier).InclusiveBetween(0.1m, 10m).WithMessage("Use a multiplier between 0.1 and 10.")
                .PrecisionScale(9, 4, true);
        });
        RuleFor(x => x.BedMultipliers)
            .Must(list => list is null || list.Select(m => m.BedServiceItemId).Distinct().Count() == list.Count)
            .WithMessage("Each bed class can have only one multiplier.");
    }
}

public sealed class SaveTariffPriceRequestValidator : AbstractValidator<SaveTariffPriceRequest>
{
    public SaveTariffPriceRequestValidator()
    {
        RuleFor(x => x.ServiceItemId).NotEmpty();
        RuleFor(x => x.Price).NonNegativeAmount();
        RuleFor(x => x.EffectiveTo)
            .Must((request, to) => to is null || to >= request.EffectiveFrom)
            .WithMessage("The end date must be on or after the start date.");
    }
}

public sealed class BulkTariffPricesRequestValidator : AbstractValidator<BulkTariffPricesRequest>
{
    public BulkTariffPricesRequestValidator()
    {
        RuleFor(x => x.Prices).NotEmpty().Must(p => p.Count <= 500).WithMessage("Import at most 500 prices at a time.");
        RuleForEach(x => x.Prices).SetValidator(new SaveTariffPriceRequestValidator());
    }
}
