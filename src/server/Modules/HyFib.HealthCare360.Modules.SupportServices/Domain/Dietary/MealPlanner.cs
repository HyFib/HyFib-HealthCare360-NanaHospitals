using System.Globalization;

namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

/// <summary>An active diet order as the planner needs it, with the patient's current ward and bed.</summary>
public sealed record DietForPlanning(
    Guid DietOrderId,
    Guid AdmissionId,
    Guid PatientId,
    Guid FacilityId,
    Guid DietTypeId,
    string DietCode,
    string DietName,
    bool IsNilByMouth,
    MealSlot Slots,
    FoodPreference Preference,
    IReadOnlyList<string> Restrictions,
    string? AllergyNotes,
    string? Instructions,
    Guid? WardId,
    string? BedLabel);

public sealed record PlannedMealSnapshot(Guid Id, Guid DietOrderId, Guid AdmissionId, DateOnly PlanDate, MealSlot Slot, MealDeliveryStatus Status);

public sealed record MealToAdd(DietForPlanning Diet, MealSlot Slot);

public sealed record MealPlanChanges(IReadOnlyList<MealToAdd> ToAdd, IReadOnlyList<Guid> ToCancel)
{
    public bool IsEmpty => ToAdd.Count == 0 && ToCancel.Count == 0;
}

/// <summary>
/// Ward-wise meal planning (DIET-02). Reconciling a day's plan with the active diet orders is idempotent, so the plan can
/// be regenerated at any time: meals still to be served are added, planned meals whose order stopped or no longer covers
/// that round are cancelled (a switch to nil-by-mouth cancels the next meal immediately), served meals are never
/// touched, and nothing is added for a round whose service time has passed.
/// </summary>
public static class MealPlanner
{
    public static MealPlanChanges Reconcile(
        DateOnly planDate, IReadOnlyCollection<DietForPlanning> diets, IReadOnlyCollection<PlannedMealSnapshot> existing, DateTimeOffset now)
    {
        var active = diets.GroupBy(d => d.DietOrderId).ToDictionary(g => g.Key, g => g.First());
        var toCancel = existing
            .Where(m => m.PlanDate == planDate && IsCancellable(m, now)
                        && (!active.TryGetValue(m.DietOrderId, out var diet) || diet.IsNilByMouth || !MealSlots.Contains(diet.Slots, m.Slot)))
            .Select(m => m.Id)
            .ToHashSet();

        var toAdd = new List<MealToAdd>();
        foreach (var diet in active.Values.Where(d => !d.IsNilByMouth))
        {
            foreach (var slot in MealSlots.Split(diet.Slots))
            {
                if (MealSlots.ServedAt(planDate, slot) <= now)
                {
                    continue;
                }

                // One meal per patient per round; and a round this order already had (even if cancelled) is never re-planned.
                var alreadyPlanned = existing.Any(m => m.PlanDate == planDate && m.Slot == slot
                                                       && (m.DietOrderId == diet.DietOrderId
                                                           || (m.AdmissionId == diet.AdmissionId && m.Status != MealDeliveryStatus.Cancelled && !toCancel.Contains(m.Id))))
                                     || toAdd.Any(a => a.Diet.AdmissionId == diet.AdmissionId && a.Slot == slot);
                if (!alreadyPlanned)
                {
                    toAdd.Add(new MealToAdd(diet, slot));
                }
            }
        }

        return new MealPlanChanges(toAdd, toCancel.ToList());
    }

    /// <summary>Planned meals to cancel from <paramref name="from"/> on (discharge): rounds already served stay as they are.</summary>
    public static IReadOnlyList<Guid> CancelFrom(IEnumerable<PlannedMealSnapshot> meals, DateTimeOffset from) =>
        meals.Where(m => IsCancellable(m, from)).Select(m => m.Id).ToList();

    private static bool IsCancellable(PlannedMealSnapshot meal, DateTimeOffset now) =>
        meal.Status == MealDeliveryStatus.Planned && MealSlots.ServedAt(meal.PlanDate, meal.Slot) > now;
}

/// <summary>Idempotency key of a daily diet charge: "{dietOrderId}:{yyyy-MM-dd}" (SourceModule "supportservices.diet").</summary>
public static class DietChargeKeys
{
    public const string Source = "supportservices.diet";

    public static string For(Guid dietOrderId, DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{dietOrderId:D}:{date:yyyy-MM-dd}");
}
