using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Packages;

public sealed record PackageListItem(
    Guid Id,
    string Code,
    string Name,
    decimal Price,
    int? LengthOfStayDays,
    Guid? TariffPlanId,
    PackageUse UsedFor,
    bool IsActive,
    int InclusionCount,
    string? HbpCode = null);

public sealed record PackageInclusionDto(
    Guid Id,
    Guid? ServiceItemId,
    string? ServiceCode,
    string? ServiceName,
    ServiceCategory? Category,
    decimal? MaxQuantity,
    decimal? MaxAmount,
    string? Notes);

public sealed record PackageDetail(
    Guid Id,
    string Code,
    string Name,
    Guid ServiceItemId,
    string? ServiceCode,
    decimal Price,
    int? LengthOfStayDays,
    Guid? TariffPlanId,
    PackageUse UsedFor,
    string? Description,
    bool IsActive,
    IReadOnlyList<PackageInclusionDto> Inclusions,
    uint Version,
    string? HbpCode = null,
    string? HbpProcedureName = null);

/// <summary>An inclusion names one service item <em>or</em> a category, with optional quantity and amount caps.</summary>
public sealed record PackageInclusionInput(Guid? ServiceItemId, ServiceCategory? Category, decimal? MaxQuantity, decimal? MaxAmount, string? Notes);

public sealed record SavePackageRequest(
    string Code,
    string Name,
    Guid ServiceItemId,
    decimal Price,
    int? LengthOfStayDays,
    Guid? TariffPlanId,
    PackageUse UsedFor,
    string? Description,
    bool IsActive,
    IReadOnlyList<PackageInclusionInput> Inclusions,
    uint? Version,
    string? HbpCode = null);

public sealed class SavePackageRequestValidator : AbstractValidator<SavePackageRequest>
{
    public SavePackageRequestValidator()
    {
        RuleFor(x => x.Code).Code();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ServiceItemId).NotEmpty();
        RuleFor(x => x.Price).NonNegativeAmount();
        RuleFor(x => x.LengthOfStayDays).InclusiveBetween(1, 365);
        RuleFor(x => x.UsedFor).IsInEnum();
        RuleFor(x => x.Description).SafeText(1000);
        RuleFor(x => x.HbpCode).MaximumLength(8).SafeText(8);
        RuleFor(x => x.Inclusions).NotNull().Must(i => i.Count <= 100).WithMessage("A package can have at most 100 inclusions.");
        RuleForEach(x => x.Inclusions).ChildRules(inclusion =>
        {
            inclusion.RuleFor(i => i).Must(i => (i.ServiceItemId is null) != (i.Category is null))
                .WithName("serviceItemId").WithMessage("An inclusion names either a service or a category.");
            inclusion.RuleFor(i => i.MaxQuantity).GreaterThan(0m).When(i => i.MaxQuantity is not null);
            inclusion.RuleFor(i => i.MaxAmount).OptionalAmount();
            inclusion.RuleFor(i => i.Notes).SafeText(300);
        });
    }
}
