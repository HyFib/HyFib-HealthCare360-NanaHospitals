using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Templates;

public sealed record TemplateMedicationRequest(
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string Dose,
    DoseFrequency Frequency,
    string? CustomFrequency,
    int DurationDays,
    string? Instructions);

/// <summary><see cref="Personal"/> templates belong to the signed-in doctor; others are shared within the speciality.</summary>
public sealed record SaveTemplateRequest(
    string Code,
    string Name,
    string? SpecialtyCode,
    bool Personal,
    string? ChiefComplaint,
    string? HistoryOfPresentIllness,
    string? Examination,
    string? Assessment,
    string? Plan,
    string? FollowUpAdvice,
    int? FollowUpDays,
    IReadOnlyList<string>? CommonComplaints,
    IReadOnlyList<Guid>? DefaultServiceItemIds,
    IReadOnlyList<TemplateMedicationRequest>? Medications,
    int DisplayOrder,
    bool IsActive,
    uint? Version);

public sealed record TemplateServiceDto(Guid ServiceItemId, string Code, string Name, ServiceCategory Category);

public sealed record TemplateMedicationDto(
    Guid Id,
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string Dose,
    DoseFrequency Frequency,
    string? CustomFrequency,
    int DurationDays,
    string? Instructions);

public sealed record TemplateListItem(Guid Id, string Code, string Name, string? SpecialtyCode, bool IsPersonal, int DisplayOrder, bool IsActive);

public sealed record TemplateDto(
    Guid Id,
    string Code,
    string Name,
    string? SpecialtyCode,
    Guid? OwnerPractitionerId,
    string? ChiefComplaint,
    string? HistoryOfPresentIllness,
    string? Examination,
    string? Assessment,
    string? Plan,
    string? FollowUpAdvice,
    int? FollowUpDays,
    IReadOnlyList<string> CommonComplaints,
    IReadOnlyList<TemplateServiceDto> DefaultOrders,
    IReadOnlyList<TemplateMedicationDto> Medications,
    int DisplayOrder,
    bool IsActive,
    uint Version);

public sealed record SaveOrderSetRequest(string Code, string Name, string? SpecialtyCode, string? Description, IReadOnlyList<Guid> ServiceItemIds, bool IsActive, uint? Version);

public sealed record OrderSetDto(Guid Id, string Code, string Name, string? SpecialtyCode, string? Description, IReadOnlyList<TemplateServiceDto> Services, bool IsActive, uint Version);

public sealed record SaveFavouriteRequest(
    FavouriteKind Kind,
    string Label,
    Guid? ServiceItemId,
    Guid? DrugItemId,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string? Dose,
    DoseFrequency? Frequency,
    string? CustomFrequency,
    int? DurationDays,
    string? Instructions,
    bool IsControlledDrug,
    int DisplayOrder);

public sealed record FavouriteDto(
    Guid Id,
    FavouriteKind Kind,
    string Label,
    Guid? ServiceItemId,
    Guid? DrugItemId,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string? Dose,
    DoseFrequency? Frequency,
    string? CustomFrequency,
    int? DurationDays,
    string? Instructions,
    bool IsControlledDrug,
    int DisplayOrder);

public sealed class SaveTemplateRequestValidator : AbstractValidator<SaveTemplateRequest>
{
    public SaveTemplateRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Matches("^[A-Z0-9][A-Z0-9-]*$").WithMessage("Use upper-case letters, digits and hyphens.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SpecialtyCode).MaximumLength(16);
        RuleFor(x => x.ChiefComplaint).SafeText(1000);
        RuleFor(x => x.HistoryOfPresentIllness).SafeText(4000);
        RuleFor(x => x.Examination).SafeText(4000);
        RuleFor(x => x.Assessment).SafeText(4000);
        RuleFor(x => x.Plan).SafeText(4000);
        RuleFor(x => x.FollowUpAdvice).SafeText(1000);
        RuleFor(x => x.FollowUpDays).InclusiveBetween(1, 365).When(x => x.FollowUpDays is not null);
        RuleFor(x => x.CommonComplaints).Must(c => c is null || c.Count <= 30).WithMessage("At most 30 complaints.");
        RuleForEach(x => x.CommonComplaints).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DefaultServiceItemIds).Must(s => s is null || s.Count <= 30).WithMessage("At most 30 default orders.");
        RuleFor(x => x.Medications).Must(m => m is null || m.Count <= 20).WithMessage("At most 20 default medicines.");
        RuleForEach(x => x.Medications).ChildRules(m =>
        {
            m.RuleFor(i => i.DrugName).NotEmpty().MaximumLength(200);
            m.RuleFor(i => i.GenericName).MaximumLength(300);
            m.RuleFor(i => i.Strength).MaximumLength(64);
            m.RuleFor(i => i.DosageForm).MaximumLength(64);
            m.RuleFor(i => i.Route).MaximumLength(32);
            m.RuleFor(i => i.Dose).NotEmpty().MaximumLength(64);
            m.RuleFor(i => i.CustomFrequency).NotEmpty().When(i => i.Frequency == DoseFrequency.Custom).MaximumLength(64);
            m.RuleFor(i => i.DurationDays).InclusiveBetween(1, 365);
            m.RuleFor(i => i.Instructions).SafeText(300);
        });
    }
}

public sealed class SaveOrderSetRequestValidator : AbstractValidator<SaveOrderSetRequest>
{
    public SaveOrderSetRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Matches("^[A-Z0-9][A-Z0-9-]*$").WithMessage("Use upper-case letters, digits and hyphens.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SpecialtyCode).MaximumLength(16);
        RuleFor(x => x.Description).SafeText(500);
        RuleFor(x => x.ServiceItemIds).NotEmpty().Must(s => s is null || s.Count <= 40).WithMessage("An order set has 1 to 40 services.");
    }
}

public sealed class SaveFavouriteRequestValidator : AbstractValidator<SaveFavouriteRequest>
{
    public SaveFavouriteRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ServiceItemId).NotNull().When(x => x.Kind == FavouriteKind.Order).WithMessage("Choose the service.");
        RuleFor(x => x.Dose).NotEmpty().When(x => x.Kind == FavouriteKind.Drug).WithMessage("Enter the usual dose.");
        RuleFor(x => x.GenericName).MaximumLength(300);
        RuleFor(x => x.Strength).MaximumLength(64);
        RuleFor(x => x.DosageForm).MaximumLength(64);
        RuleFor(x => x.Route).MaximumLength(32);
        RuleFor(x => x.Dose).MaximumLength(64);
        RuleFor(x => x.CustomFrequency).MaximumLength(64);
        RuleFor(x => x.DurationDays).InclusiveBetween(1, 365).When(x => x.DurationDays is not null);
        RuleFor(x => x.Instructions).SafeText(300);
    }
}
