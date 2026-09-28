using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

/// <summary>Meal rounds of the hospital kitchen. Flags, so a diet order can list the rounds it includes.</summary>
[Flags]
public enum MealSlot
{
    None = 0,
    EarlyMorning = 1,
    Breakfast = 2,
    MidMorning = 4,
    Lunch = 8,
    Evening = 16,
    Dinner = 32,
    Bedtime = 64,
}

/// <summary>
/// Service times of the meal rounds (IST): 06:00 early-morning tea/milk, 08:00 breakfast, 11:00 mid-morning,
/// 13:00 lunch, 16:30 evening tea/snack, 19:30 dinner, 21:30 bedtime milk. A slot is plannable until it is served.
/// </summary>
public static class MealSlots
{
    public const MealSlot Standard = MealSlot.EarlyMorning | MealSlot.Breakfast | MealSlot.Lunch | MealSlot.Evening | MealSlot.Dinner;

    /// <summary>Small frequent feeds (diabetic, renal, liquid diets).</summary>
    public const MealSlot All = Standard | MealSlot.MidMorning | MealSlot.Bedtime;

    private static readonly MealSlot[] Ordered =
        [MealSlot.EarlyMorning, MealSlot.Breakfast, MealSlot.MidMorning, MealSlot.Lunch, MealSlot.Evening, MealSlot.Dinner, MealSlot.Bedtime];

    public static IReadOnlyList<MealSlot> Split(MealSlot slots) => Ordered.Where(s => (slots & s) == s).ToList();

    public static bool Contains(MealSlot slots, MealSlot slot) => slot != MealSlot.None && (slots & slot) == slot;

    public static TimeOnly ServiceTime(MealSlot slot) => slot switch
    {
        MealSlot.EarlyMorning => new TimeOnly(6, 0),
        MealSlot.Breakfast => new TimeOnly(8, 0),
        MealSlot.MidMorning => new TimeOnly(11, 0),
        MealSlot.Lunch => new TimeOnly(13, 0),
        MealSlot.Evening => new TimeOnly(16, 30),
        MealSlot.Dinner => new TimeOnly(19, 30),
        MealSlot.Bedtime => new TimeOnly(21, 30),
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Choose a single meal slot."),
    };

    public static DateTimeOffset ServedAt(DateOnly date, MealSlot slot) => IndiaClock.AtLocal(date, ServiceTime(slot));
}
