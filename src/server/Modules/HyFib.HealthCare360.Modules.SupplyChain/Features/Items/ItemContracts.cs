using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Items;

public sealed record ItemCategoryDto(Guid Id, string Code, string Name, ItemType DefaultType, string? Description, bool IsActive, uint Version);

public sealed record SaveItemCategoryRequest(string Code, string Name, ItemType DefaultType, string? Description, bool IsActive, uint? Version);

public sealed record ItemListItem(
    Guid Id,
    string Code,
    string Name,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    ItemType Type,
    string? CategoryName,
    DrugSchedule Schedule,
    bool IsControlled,
    bool IsColdChain,
    string BaseUnit,
    string PurchaseUnit,
    decimal GstRate,
    decimal ReorderLevel,
    bool IsActive);

public sealed record ItemDetail(
    Guid Id,
    string Code,
    string Name,
    string? GenericName,
    string? BrandName,
    ItemType Type,
    Guid? CategoryId,
    string? Strength,
    string? DosageForm,
    string? PackSize,
    string BaseUnit,
    string PurchaseUnit,
    decimal PurchaseUnitFactor,
    string? HsnCode,
    decimal GstRate,
    DrugSchedule Schedule,
    bool IsControlled,
    bool IsColdChain,
    VedClass VedClass,
    decimal ReorderLevel,
    decimal ReorderQuantity,
    decimal MinimumStock,
    decimal MaximumStock,
    string? Manufacturer,
    string? Barcode,
    bool IsActive,
    bool RequiresPrescription,
    bool RequiresRegister,
    uint Version,
    string? Specification,
    decimal? CeilingPrice);

public sealed record SaveItemRequest(
    string Code,
    string Name,
    string? GenericName,
    string? BrandName,
    ItemType Type,
    Guid? CategoryId,
    string? Strength,
    string? DosageForm,
    string? PackSize,
    string BaseUnit,
    string PurchaseUnit,
    decimal PurchaseUnitFactor,
    string? HsnCode,
    decimal GstRate,
    DrugSchedule Schedule,
    bool IsControlled,
    bool IsColdChain,
    VedClass VedClass,
    decimal ReorderLevel,
    decimal ReorderQuantity,
    decimal MinimumStock,
    decimal MaximumStock,
    string? Manufacturer,
    string? Barcode,
    bool IsActive,
    uint? Version,
    string? Specification = null,
    decimal? CeilingPrice = null);

/// <summary>Picker row: the item plus its sellable stock in the chosen store (when a store is given).</summary>
public sealed record ItemLookup(
    Guid Id,
    string Code,
    string Name,
    string? GenericName,
    string? Strength,
    ItemType Type,
    DrugSchedule Schedule,
    bool IsControlled,
    bool IsColdChain,
    string BaseUnit,
    string PurchaseUnit,
    decimal PurchaseUnitFactor,
    decimal GstRate,
    decimal? Available,
    decimal? Mrp,
    DateOnly? NearestExpiry);

internal static class CatalogRules
{
    public static bool ValidCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && code.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/');
}

public sealed class SaveItemCategoryRequestValidator : AbstractValidator<SaveItemCategoryRequest>
{
    public SaveItemCategoryRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Must(CatalogRules.ValidCode).WithMessage("Use letters, digits, '-', '_' or '/'.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).SafeText(300);
    }
}

public sealed class SaveItemRequestValidator : AbstractValidator<SaveItemRequest>
{
    public SaveItemRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Must(CatalogRules.ValidCode).WithMessage("Use letters, digits, '-', '_' or '/'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.GenericName).SafeText(200);
        RuleFor(x => x.BrandName).SafeText(120);
        RuleFor(x => x.Strength).MaximumLength(60);
        RuleFor(x => x.DosageForm).MaximumLength(60);
        RuleFor(x => x.PackSize).MaximumLength(60);
        RuleFor(x => x.BaseUnit).NotEmpty().MaximumLength(16);
        RuleFor(x => x.PurchaseUnit).NotEmpty().MaximumLength(16);
        RuleFor(x => x.PurchaseUnitFactor).GreaterThan(0).LessThanOrEqualTo(100_000)
            .WithMessage("Enter how many base units make one purchase unit (for example 10 tablets per strip).");
        RuleFor(x => x.HsnCode).Matches("^[0-9]{4}([0-9]{2}){0,2}$").When(x => !string.IsNullOrWhiteSpace(x.HsnCode))
            .WithMessage("HSN code has 4, 6 or 8 digits.");
        RuleFor(x => x.GstRate).Must(GstCalculator.IsStandardRate).WithMessage("Use a notified GST rate (0, 0.25, 3, 5, 12, 18, 28 or 40%).");
        RuleFor(x => x.ReorderLevel).GreaterThanOrEqualTo(0);
        RuleFor(x => x.ReorderQuantity).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MinimumStock).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaximumStock).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Manufacturer).SafeText(120);
        RuleFor(x => x.Specification).SafeText(200);
        RuleFor(x => x.CeilingPrice).GreaterThan(0).LessThanOrEqualTo(10_000_000).When(x => x.CeilingPrice is not null);
        RuleFor(x => x.Barcode).MaximumLength(64);
    }
}
