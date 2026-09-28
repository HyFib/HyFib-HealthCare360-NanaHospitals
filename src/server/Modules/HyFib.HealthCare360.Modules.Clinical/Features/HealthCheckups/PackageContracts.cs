using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;

/// <summary>
/// A package line: an investigation (<see cref="ServiceItemId"/> from the service catalogue) or a consultation in a
/// department (<see cref="DepartmentCode"/>; billed under CONS-NEW by default). Item rules narrow who gets the line.
/// </summary>
public sealed record PackageItemRequest(PackageItemKind Kind, Guid? ServiceItemId, string? DepartmentCode, PackageGender Gender, int? MinAgeYears);

public sealed record SavePackageRequest(
    string Code,
    string Name,
    string? Description,
    PackageGender Gender,
    int? MinAgeYears,
    int? MaxAgeYears,
    decimal Price,
    bool IsActive,
    IReadOnlyList<PackageItemRequest> Items,
    uint? Version);

public sealed record PackageItemDto(
    Guid Id,
    PackageItemKind Kind,
    Guid? ServiceItemId,
    string ServiceCode,
    string Name,
    ServiceCategory Category,
    string? DepartmentCode,
    PackageGender Gender,
    int? MinAgeYears,
    int Sequence);

public sealed record PackageDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    PackageGender Gender,
    int? MinAgeYears,
    int? MaxAgeYears,
    decimal Price,
    Guid? ServiceItemId,
    bool IsActive,
    IReadOnlyList<PackageItemDto> Items,
    uint Version);

public sealed record PackageEligibility(bool IsEligible, string? Reason, IReadOnlyList<string> ExcludedItems);

public sealed class SavePackageRequestValidator : AbstractValidator<SavePackageRequest>
{
    public SavePackageRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Matches("^HC-[A-Z0-9-]+$").WithMessage("Package codes look like HC-EXEC (upper case).");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).SafeText(1000);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0).LessThan(10_000_000);
        RuleFor(x => x.MinAgeYears).InclusiveBetween(0, 120).When(x => x.MinAgeYears is not null);
        RuleFor(x => x.MaxAgeYears).InclusiveBetween(0, 130).When(x => x.MaxAgeYears is not null);
        RuleFor(x => x.MaxAgeYears).GreaterThanOrEqualTo(x => x.MinAgeYears).When(x => x.MinAgeYears is not null && x.MaxAgeYears is not null)
            .WithMessage("Maximum age must not be below the minimum age.");
        RuleFor(x => x.Items).NotEmpty().WithMessage("Add at least one test or consultation.").Must(i => i is null || i.Count <= 60);
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ServiceItemId).NotNull().When(i => i.Kind == PackageItemKind.Investigation).WithMessage("Choose the test or service.");
            item.RuleFor(i => i.DepartmentCode).NotEmpty().When(i => i.Kind == PackageItemKind.Consultation).WithMessage("Choose the consulting department.");
            item.RuleFor(i => i.DepartmentCode).MaximumLength(16);
            item.RuleFor(i => i.MinAgeYears).InclusiveBetween(0, 120).When(i => i.MinAgeYears is not null);
        });
    }
}
