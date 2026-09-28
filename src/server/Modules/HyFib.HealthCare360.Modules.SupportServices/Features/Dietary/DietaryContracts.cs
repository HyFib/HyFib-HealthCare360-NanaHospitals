using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;

public sealed record DietTypeDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    DietCategory Category,
    Guid? ChargeServiceItemId,
    string? ChargeServiceCode,
    MealSlot DefaultMealSlots,
    IReadOnlyList<MealSlot> DefaultMeals,
    int DisplayOrder,
    bool IsActive,
    uint Version);

public sealed record SaveDietTypeRequest(
    string Code,
    string Name,
    string? Description,
    DietCategory Category,
    Guid? ChargeServiceItemId,
    MealSlot DefaultMealSlots,
    int DisplayOrder,
    bool IsActive,
    uint? Version);

public sealed record DietOrderDto(
    Guid Id,
    Guid AdmissionId,
    string? AdmissionNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    string? WardName,
    string? BedLabel,
    Guid DietTypeId,
    string? DietCode,
    string? DietName,
    FoodPreference Preference,
    IReadOnlyList<string> Restrictions,
    string? AllergyNotes,
    string? Instructions,
    int? FluidRestrictionMl,
    int? CalorieTargetKcal,
    MealSlot MealSlots,
    IReadOnlyList<MealSlot> Meals,
    DateOnly StartDate,
    DateOnly? EndDate,
    DietOrderStatus Status,
    DietOrderSource Source,
    string? OrderedByName,
    bool NeedsReview,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StoppedAt,
    string? StopReason);

public sealed record CreateDietOrderRequest(
    Guid AdmissionId,
    Guid DietTypeId,
    FoodPreference Preference,
    IReadOnlyList<string>? Restrictions,
    string? AllergyNotes,
    string? Instructions,
    int? FluidRestrictionMl,
    int? CalorieTargetKcal,
    MealSlot? MealSlots,
    DateOnly? StartDate,
    Guid? OrderedByPractitionerId);

public sealed record StopDietOrderRequest(string Reason);

public sealed record GenerateMealPlanRequest(DateOnly? Date);

public sealed record MealPlanResult(DateOnly Date, int MealsAdded, int MealsCancelled, int MealsPlanned, int ChargesPosted);

public sealed record PlannedMealDto(
    Guid Id,
    DateOnly PlanDate,
    MealSlot Slot,
    TimeOnly ServiceTime,
    Guid AdmissionId,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? WardId,
    string? BedLabel,
    string DietCode,
    string? DietName,
    FoodPreference Preference,
    IReadOnlyList<string> Restrictions,
    string? AllergyNotes,
    string? Instructions,
    MealDeliveryStatus Status,
    DateTimeOffset? DeliveredAt,
    string? DeliveredByName,
    string? Remarks,
    uint Version);

public sealed record WardMealPlan(Guid? WardId, string? WardName, int Meals, IReadOnlyList<PlannedMealDto> Items);

/// <summary>Kitchen production line: how many of one diet to cook for one round, split by food preference.</summary>
public sealed record ProductionLine(
    MealSlot Slot,
    TimeOnly ServiceTime,
    string DietCode,
    string? DietName,
    int Vegetarian,
    int NonVegetarian,
    int Eggetarian,
    int Jain,
    int Total,
    IReadOnlyList<string> SpecialNotes);

public sealed record ProductionSummary(DateOnly Date, int TotalMeals, IReadOnlyList<ProductionLine> Lines);

public sealed record RecordDeliveryRequest(MealDeliveryStatus Status, string? Remarks, uint Version);

public sealed record BulkDeliveryRequest(DateOnly Date, MealSlot Slot, Guid WardId, MealDeliveryStatus Status);

public sealed record BulkDeliveryResult(int Updated);

internal static class DietRules
{
    public const int MaxRestrictions = 10;
}

public sealed class SaveDietTypeRequestValidator : AbstractValidator<SaveDietTypeRequest>
{
    public SaveDietTypeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits or dashes.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).SafeText(500);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.DefaultMealSlots).Must(s => s is > MealSlot.None and <= MealSlots.All).WithMessage("Choose at least one meal round.");
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 10_000);
    }
}

public sealed class CreateDietOrderRequestValidator : AbstractValidator<CreateDietOrderRequest>
{
    public CreateDietOrderRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.AdmissionId).NotEmpty();
        RuleFor(x => x.DietTypeId).NotEmpty();
        RuleFor(x => x.Preference).IsInEnum();
        RuleFor(x => x.Restrictions).Must(r => r is null || r.Count <= DietRules.MaxRestrictions).WithMessage("List at most 10 restrictions.");
        RuleForEach(x => x.Restrictions).NotEmpty().MaximumLength(50);
        RuleFor(x => x.AllergyNotes).SafeText(500);
        RuleFor(x => x.Instructions).SafeText(500);
        RuleFor(x => x.FluidRestrictionMl).InclusiveBetween(100, 5000);
        RuleFor(x => x.CalorieTargetKcal).InclusiveBetween(300, 5000);
        RuleFor(x => x.MealSlots).Must(s => s is null || (s > MealSlot.None && s <= MealSlots.All)).WithMessage("Choose at least one meal round.");
        RuleFor(x => x.StartDate)
            .Must(d => d is null || (d >= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1) && d <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(7)))
            .WithMessage("The diet can start today or within the next 7 days.");
    }
}

public sealed class StopDietOrderRequestValidator : AbstractValidator<StopDietOrderRequest>
{
    public StopDietOrderRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class RecordDeliveryRequestValidator : AbstractValidator<RecordDeliveryRequest>
{
    public RecordDeliveryRequestValidator()
    {
        RuleFor(x => x.Status).Must(s => s is MealDeliveryStatus.Delivered or MealDeliveryStatus.NotTaken or MealDeliveryStatus.Returned)
            .WithMessage("Mark the meal as delivered, not taken or returned.");
        RuleFor(x => x.Remarks).SafeText(300);
        RuleFor(x => x.Remarks).NotEmpty().When(x => x.Status == MealDeliveryStatus.NotTaken).WithMessage("Say why the meal was not taken.");
    }
}

public sealed class BulkDeliveryRequestValidator : AbstractValidator<BulkDeliveryRequest>
{
    public BulkDeliveryRequestValidator()
    {
        RuleFor(x => x.WardId).NotEmpty();
        RuleFor(x => x.Slot).Must(s => MealSlots.Split(s).Count == 1).WithMessage("Choose one meal round.");
        RuleFor(x => x.Status).Equal(MealDeliveryStatus.Delivered).WithMessage("Only deliveries can be recorded for a whole ward at once.");
    }
}
