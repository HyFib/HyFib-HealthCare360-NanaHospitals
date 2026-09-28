using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class MealPlannerTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly Guid Admission = Guid.NewGuid();

    private static DateTimeOffset At(int hour, int minute = 0, int day = 25) => IndiaClock.AtLocal(new DateOnly(2026, 9, day), new TimeOnly(hour, minute));

    private static DietForPlanning Diet(Guid? orderId = null, MealSlot slots = MealSlots.Standard, bool nilByMouth = false, string code = "NORMAL") =>
        new(orderId ?? Guid.NewGuid(), Admission, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), code, code, nilByMouth, slots, FoodPreference.Vegetarian,
            ["No onion"], null, null, Guid.NewGuid(), "GWM-03");

    private static PlannedMealSnapshot Meal(Guid orderId, MealSlot slot, MealDeliveryStatus status = MealDeliveryStatus.Planned, DateOnly? date = null) =>
        new(Guid.NewGuid(), orderId, Admission, date ?? Today, slot, status);

    [Fact]
    public void Every_round_of_the_diet_is_planned_for_a_future_day()
    {
        var changes = MealPlanner.Reconcile(Today.AddDays(1), [Diet()], [], At(20));

        changes.ToAdd.Select(a => a.Slot).ShouldBe([MealSlot.EarlyMorning, MealSlot.Breakfast, MealSlot.Lunch, MealSlot.Evening, MealSlot.Dinner]);
        changes.ToCancel.ShouldBeEmpty();
    }

    [Fact]
    public void Rounds_already_served_today_are_not_planned()
    {
        var changes = MealPlanner.Reconcile(Today, [Diet()], [], At(12));

        changes.ToAdd.Select(a => a.Slot).ShouldBe([MealSlot.Lunch, MealSlot.Evening, MealSlot.Dinner]);
    }

    [Fact]
    public void Regenerating_an_up_to_date_plan_changes_nothing()
    {
        var diet = Diet();
        PlannedMealSnapshot[] existing = [.. MealSlots.Split(diet.Slots).Select(s => Meal(diet.DietOrderId, s))];

        MealPlanner.Reconcile(Today, [diet], existing, At(5)).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_switch_to_nil_by_mouth_cancels_the_next_meal_at_once()
    {
        var old = Guid.NewGuid();
        var npo = Diet(nilByMouth: true, code: "NPO");
        var lunch = Meal(old, MealSlot.Lunch);
        var breakfast = Meal(old, MealSlot.Breakfast);

        var changes = MealPlanner.Reconcile(Today, [npo], [breakfast, lunch], At(12, 50));

        changes.ToCancel.ShouldBe([lunch.Id]);
        changes.ToAdd.ShouldBeEmpty();
    }

    [Fact]
    public void A_new_order_replaces_the_old_orders_meals_still_to_come()
    {
        var old = Guid.NewGuid();
        var diabetic = Diet(slots: MealSlot.Lunch | MealSlot.Dinner, code: "DIABETIC");
        var oldDinner = Meal(old, MealSlot.Dinner);

        var changes = MealPlanner.Reconcile(Today, [diabetic], [oldDinner], At(10));

        changes.ToCancel.ShouldBe([oldDinner.Id]);
        changes.ToAdd.Select(a => (a.Slot, a.Diet.DietCode)).ShouldBe([(MealSlot.Lunch, "DIABETIC"), (MealSlot.Dinner, "DIABETIC")]);
    }

    [Fact]
    public void A_round_already_served_is_not_served_twice_after_a_diet_change()
    {
        var old = Guid.NewGuid();
        var soft = Diet(slots: MealSlot.Lunch, code: "SOFT");
        var servedLunch = Meal(old, MealSlot.Lunch, MealDeliveryStatus.Delivered);

        var changes = MealPlanner.Reconcile(Today, [soft], [servedLunch], At(12, 30));

        changes.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_cancelled_round_of_an_order_is_never_planned_again()
    {
        var diet = Diet(slots: MealSlot.Dinner);
        var cancelledDinner = Meal(diet.DietOrderId, MealSlot.Dinner, MealDeliveryStatus.Cancelled);

        MealPlanner.Reconcile(Today, [diet], [cancelledDinner], At(10)).ToAdd.ShouldBeEmpty();
    }

    [Fact]
    public void Discharge_cancels_only_meals_served_after_the_patient_leaves()
    {
        var order = Guid.NewGuid();
        var lunch = Meal(order, MealSlot.Lunch);
        var dinner = Meal(order, MealSlot.Dinner);
        var tomorrowsBreakfast = Meal(order, MealSlot.Breakfast, date: Today.AddDays(1));
        var servedBreakfast = Meal(order, MealSlot.Breakfast, MealDeliveryStatus.Delivered);

        var cancel = MealPlanner.CancelFrom([lunch, dinner, tomorrowsBreakfast, servedBreakfast], At(14));

        cancel.ShouldBe([dinner.Id, tomorrowsBreakfast.Id], ignoreOrder: true);
    }

    [Fact]
    public void Meal_rounds_are_split_in_service_order()
    {
        MealSlots.Split(MealSlot.Dinner | MealSlot.EarlyMorning | MealSlot.Lunch).ShouldBe([MealSlot.EarlyMorning, MealSlot.Lunch, MealSlot.Dinner]);
        MealSlots.ServiceTime(MealSlot.Evening).ShouldBe(new TimeOnly(16, 30));
        Should.Throw<ArgumentOutOfRangeException>(() => MealSlots.ServiceTime(MealSlot.Lunch | MealSlot.Dinner));
    }

    [Theory]
    [InlineData(MealDeliveryStatus.Planned, MealDeliveryStatus.Delivered, true)]
    [InlineData(MealDeliveryStatus.Planned, MealDeliveryStatus.NotTaken, true)]
    [InlineData(MealDeliveryStatus.Delivered, MealDeliveryStatus.Returned, true)]
    [InlineData(MealDeliveryStatus.Planned, MealDeliveryStatus.Returned, false)]
    [InlineData(MealDeliveryStatus.NotTaken, MealDeliveryStatus.Delivered, false)]
    [InlineData(MealDeliveryStatus.Cancelled, MealDeliveryStatus.Delivered, false)]
    public void Meal_delivery_transitions(MealDeliveryStatus current, MealDeliveryStatus outcome, bool allowed) =>
        MealDeliveryRules.Validate(current, outcome).IsSuccess.ShouldBe(allowed);

    [Fact]
    public void Recording_a_delivery_stamps_who_and_when()
    {
        var meal = new PlannedMeal { Status = MealDeliveryStatus.Planned };

        meal.RecordDelivery(MealDeliveryStatus.Delivered, null, Guid.NewGuid(), "Pantry — Ward 1", At(13, 5)).IsSuccess.ShouldBeTrue();

        meal.Status.ShouldBe(MealDeliveryStatus.Delivered);
        meal.DeliveredAt.ShouldBe(At(13, 5));
        meal.DeliveredByName.ShouldBe("Pantry — Ward 1");
    }

    [Fact]
    public void Diet_charge_keys_are_order_and_date() =>
        DietChargeKeys.For(Guid.Parse("0199a7d2-3c4f-7a10-9d2e-6b1f0c8e4a55"), Today).ShouldBe("0199a7d2-3c4f-7a10-9d2e-6b1f0c8e4a55:2026-09-25");
}
