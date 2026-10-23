using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>
/// The kitchen production list (DIET-03): for each meal round, how many meals of each diet to cook, by food preference, with
/// the restrictions and allergies by bed. It counts the day's plan as the planner keeps it: a cancelled meal is not cooked,
/// a patient kept nil by mouth has no meals, and when a diet changes during the day the rounds already served stay with the
/// old diet while the rest are cooked to the new one.
/// </summary>
public sealed class KitchenProductionTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);

    private static DateTimeOffset At(int hour, int minute = 0) => IndiaClock.AtLocal(Day, new TimeOnly(hour, minute));

    private static PlannedMeal Meal(
        MealSlot slot, string diet, FoodPreference preference, MealDeliveryStatus status = MealDeliveryStatus.Planned, string? bed = null,
        string[]? restrictions = null, string? allergy = null) => new()
        {
            PlanDate = Day,
            Slot = slot,
            DietCode = diet,
            DietName = diet + " diet",
            Preference = preference,
            Status = status,
            BedLabel = bed,
            Restrictions = [.. restrictions ?? []],
            AllergyNotes = allergy,
        };

    private static DietForPlanning Diet(
        string code, FoodPreference preference, string bed, bool nilByMouth = false, MealSlot slots = MealSlots.Standard, Guid? admission = null,
        string[]? restrictions = null) =>
        new(Guid.NewGuid(), admission ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), code, code + " diet", nilByMouth, slots,
            preference, restrictions ?? [], null, null, Guid.NewGuid(), bed);

    [Fact]
    public void Counts_each_rounds_meals_of_a_diet_by_food_preference_with_the_notes_by_bed()
    {
        var lines = KitchenProduction.Count([
            Meal(MealSlot.Lunch, "NORMAL", FoodPreference.Vegetarian),
            Meal(MealSlot.Lunch, "NORMAL", FoodPreference.Vegetarian, bed: "GW-02", restrictions: ["No onion", "No garlic"]),
            Meal(MealSlot.Lunch, "NORMAL", FoodPreference.NonVegetarian, bed: "GW-05", restrictions: ["Soft rice"], allergy: "Peanuts"),
            Meal(MealSlot.Lunch, "NORMAL", FoodPreference.Eggetarian),
            Meal(MealSlot.Lunch, "NORMAL", FoodPreference.Jain, restrictions: ["No root vegetables"]),
            Meal(MealSlot.Lunch, "DIABETIC", FoodPreference.Vegetarian),
            Meal(MealSlot.Breakfast, "NORMAL", FoodPreference.Vegetarian, MealDeliveryStatus.Delivered),
            Meal(MealSlot.EarlyMorning, "NORMAL", FoodPreference.Vegetarian, MealDeliveryStatus.NotTaken),
        ]);

        // Rounds in service order, diets by code within a round; a meal served or refused was still cooked.
        lines.Select(l => (l.Slot, l.DietCode, l.Vegetarian, l.NonVegetarian, l.Eggetarian, l.Jain, l.Total)).ShouldBe([
            (MealSlot.EarlyMorning, "NORMAL", 1, 0, 0, 0, 1),
            (MealSlot.Breakfast, "NORMAL", 1, 0, 0, 0, 1),
            (MealSlot.Lunch, "DIABETIC", 1, 0, 0, 0, 1),
            (MealSlot.Lunch, "NORMAL", 2, 1, 1, 1, 5),
        ]);
        lines[3].DietName.ShouldBe("NORMAL diet");
        lines[3].SpecialNotes.ShouldBe(["GW-02: No onion, No garlic", "GW-05: Soft rice (allergy: Peanuts)", "No bed: No root vegetables"]);
        lines[2].SpecialNotes.ShouldBeEmpty();
    }

    [Fact]
    public void A_cancelled_meal_is_not_cooked()
    {
        var lines = KitchenProduction.Count([
            Meal(MealSlot.Dinner, "NORMAL", FoodPreference.Vegetarian),
            Meal(MealSlot.Dinner, "NORMAL", FoodPreference.Vegetarian, MealDeliveryStatus.Cancelled, bed: "GW-07", restrictions: ["No sugar"]),
            Meal(MealSlot.Dinner, "SOFT", FoodPreference.Jain, MealDeliveryStatus.Cancelled),
        ]);

        var dinner = lines.ShouldHaveSingleItem();
        (dinner.DietCode, dinner.Vegetarian, dinner.Total).ShouldBe(("NORMAL", 1, 1));
        dinner.SpecialNotes.ShouldBeEmpty();
    }

    [Fact]
    public void Counts_a_day_with_a_patient_nil_by_mouth_and_a_diet_change_at_ten()
    {
        // Planned at five in the morning: Anand on a normal diet, Bina on a normal Jain diet without onion, and Chitra kept nil
        // by mouth for her operation.
        var anand = Diet("NORMAL", FoodPreference.Vegetarian, "GW-01");
        var bina = Diet("NORMAL", FoodPreference.Jain, "GW-02", restrictions: ["No onion"]);
        var chitra = Diet("NPO", FoodPreference.Vegetarian, "GW-03", nilByMouth: true);
        var meals = new List<PlannedMeal>();
        Plan(meals, [anand, bina, chitra], At(5));

        // Anand has his breakfast; at ten the doctor puts him on a diabetic diet, and the plan is brought up to date.
        meals.Single(m => m.DietOrderId == anand.DietOrderId && m.Slot == MealSlot.Breakfast)
            .RecordDelivery(MealDeliveryStatus.Delivered, null, null, "Pantry", At(8, 10)).IsSuccess.ShouldBeTrue();
        var diabetic = Diet("DIABETIC", FoodPreference.Vegetarian, "GW-01", admission: anand.AdmissionId,
            slots: MealSlot.Breakfast | MealSlot.Lunch | MealSlot.Evening | MealSlot.Dinner);
        Plan(meals, [diabetic, bina, chitra], At(10));

        var lines = KitchenProduction.Count(meals);

        // The rounds Anand already had stay normal; his later ones are cooked diabetic; Chitra has nothing cooked.
        lines.Select(l => (l.Slot, l.DietCode, l.Vegetarian, l.Jain, l.Total)).ShouldBe([
            (MealSlot.EarlyMorning, "NORMAL", 1, 1, 2),
            (MealSlot.Breakfast, "NORMAL", 1, 1, 2),
            (MealSlot.Lunch, "DIABETIC", 1, 0, 1),
            (MealSlot.Lunch, "NORMAL", 0, 1, 1),
            (MealSlot.Evening, "DIABETIC", 1, 0, 1),
            (MealSlot.Evening, "NORMAL", 0, 1, 1),
            (MealSlot.Dinner, "DIABETIC", 1, 0, 1),
            (MealSlot.Dinner, "NORMAL", 0, 1, 1),
        ]);
        lines.Sum(l => l.Total).ShouldBe(10);
        lines.Where(l => l.DietCode == "NORMAL").ShouldAllBe(l => l.SpecialNotes.SequenceEqual(new[] { "GW-02: No onion" }));

        // His normal lunch, evening snack and dinner were cancelled, not cooked.
        meals.Where(m => m.Status == MealDeliveryStatus.Cancelled).Select(m => (m.DietCode, m.Slot))
            .ShouldBe([("NORMAL", MealSlot.Lunch), ("NORMAL", MealSlot.Evening), ("NORMAL", MealSlot.Dinner)], ignoreOrder: true);
    }

    /// <summary>Brings the day's plan up to date with the diets at <paramref name="now"/>, as the meal plan service does.</summary>
    private static void Plan(List<PlannedMeal> meals, DietForPlanning[] diets, DateTimeOffset now)
    {
        var existing = meals.Select(m => new PlannedMealSnapshot(m.Id, m.DietOrderId, m.AdmissionId, m.PlanDate, m.Slot, m.Status)).ToList();
        var changes = MealPlanner.Reconcile(Day, diets, existing, now);
        foreach (var meal in meals.Where(m => changes.ToCancel.Contains(m.Id)))
        {
            meal.Cancel("Diet order changed or stopped.");
        }

        meals.AddRange(changes.ToAdd.Select(add => new PlannedMeal
        {
            PlanDate = Day,
            Slot = add.Slot,
            DietOrderId = add.Diet.DietOrderId,
            AdmissionId = add.Diet.AdmissionId,
            PatientId = add.Diet.PatientId,
            BedLabel = add.Diet.BedLabel,
            DietTypeId = add.Diet.DietTypeId,
            DietCode = add.Diet.DietCode,
            DietName = add.Diet.DietName,
            Preference = add.Diet.Preference,
            Restrictions = [.. add.Diet.Restrictions],
            AllergyNotes = add.Diet.AllergyNotes,
            Status = MealDeliveryStatus.Planned,
        }));
    }
}
