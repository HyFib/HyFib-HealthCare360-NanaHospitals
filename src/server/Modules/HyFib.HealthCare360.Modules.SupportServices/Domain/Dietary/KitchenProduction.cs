namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

/// <summary>One line of the kitchen production list: a round's meals of one diet, by food preference, with the special notes.</summary>
public sealed record ProductionCount(
    MealSlot Slot,
    string DietCode,
    string? DietName,
    int Vegetarian,
    int NonVegetarian,
    int Eggetarian,
    int Jain,
    int Total,
    IReadOnlyList<string> SpecialNotes);

/// <summary>
/// The kitchen production list (DIET-03): how many meals of each diet to cook for each round of the day, split by food
/// preference, with every restriction and allergy by bed. A cancelled meal (its diet changed or stopped, or the patient was
/// discharged) is not cooked. A patient kept nil by mouth has no meals to count: the planner plans none
/// (<see cref="MealPlanner"/>). Rounds come in service order, and diets by code within a round.
/// </summary>
public static class KitchenProduction
{
    public static IReadOnlyList<ProductionCount> Count(IEnumerable<PlannedMeal> meals) =>
        meals
            .Where(m => m.Status != MealDeliveryStatus.Cancelled)
            .GroupBy(m => new { m.Slot, m.DietCode })
            .Select(g => new ProductionCount(
                g.Key.Slot,
                g.Key.DietCode,
                g.First().DietName,
                g.Count(m => m.Preference == FoodPreference.Vegetarian),
                g.Count(m => m.Preference == FoodPreference.NonVegetarian),
                g.Count(m => m.Preference == FoodPreference.Eggetarian),
                g.Count(m => m.Preference == FoodPreference.Jain),
                g.Count(),
                g.Where(m => m.Restrictions.Count > 0 || !string.IsNullOrWhiteSpace(m.AllergyNotes))
                    .Select(m => $"{m.BedLabel ?? "No bed"}: {string.Join(", ", m.Restrictions)}{(string.IsNullOrWhiteSpace(m.AllergyNotes) ? string.Empty : $" (allergy: {m.AllergyNotes})")}")
                    .ToList()))
            .OrderBy(l => l.Slot).ThenBy(l => l.DietCode, StringComparer.Ordinal)
            .ToList();
}
