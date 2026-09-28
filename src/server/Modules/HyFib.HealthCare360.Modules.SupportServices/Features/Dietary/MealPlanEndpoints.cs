using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;

/// <summary>/api/v1/dietary — ward-wise meal plans, kitchen production list and meal delivery tracking (DIET-02..05).</summary>
internal static class MealPlanEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dietary").WithTags("Dietary");

        group.MapPost("/meal-plans/generate", async (GenerateMealPlanRequest r, MealPlanService plans, TimeProvider time, CancellationToken ct) =>
                TypedResults.Ok(await plans.GenerateAsync(r.Date ?? time.Today(), ct)))
            .RequirePermission(SupportServicesPermissions.DietMeals).WithValidation<GenerateMealPlanRequest>();
        group.MapGet("/meal-plans", async (DateOnly? date, Guid? wardId, MealSlot? slot, MealPlanService plans, TimeProvider time, CancellationToken ct) =>
                TypedResults.Ok(await plans.WardPlanAsync(date ?? time.Today(), wardId, slot, ct)))
            .RequireAnyPermission(SupportServicesPermissions.DietMeals, SupportServicesPermissions.DietOrdersRead);
        group.MapGet("/meal-plans/production", async (DateOnly? date, MealPlanService plans, TimeProvider time, CancellationToken ct) =>
                TypedResults.Ok(await plans.ProductionAsync(date ?? time.Today(), ct)))
            .RequirePermission(SupportServicesPermissions.DietMeals);
        group.MapPost("/meals/{id:guid}/delivery", async (Guid id, RecordDeliveryRequest r, MealPlanService plans, CancellationToken ct) =>
                (await plans.RecordDeliveryAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.DietMeals).WithValidation<RecordDeliveryRequest>();
        group.MapPost("/meal-plans/deliveries", async (BulkDeliveryRequest r, MealPlanService plans, CancellationToken ct) =>
                TypedResults.Ok(new BulkDeliveryResult(await plans.DeliverWardRoundAsync(r, ct))))
            .RequirePermission(SupportServicesPermissions.DietMeals).WithValidation<BulkDeliveryRequest>();
    }
}

public sealed class GenerateMealPlanRequestValidator : AbstractValidator<GenerateMealPlanRequest>
{
    public GenerateMealPlanRequestValidator(TimeProvider time) =>
        RuleFor(x => x.Date)
            .Must(d => d is null || (d >= time.Today().AddDays(-1) && d <= time.Today().AddDays(2)))
            .WithMessage("Meal plans can be generated from yesterday up to two days ahead.");
}
