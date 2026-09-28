namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// Storage life and preparation rules for blood components (BB-03/05): whole blood and PRBC 35 days (CPDA-1,
/// 2–6 °C), FFP 365 days (≤ −30 °C), platelets 5 days (20–24 °C with agitation), cryoprecipitate 365 days.
/// </summary>
public static class ComponentShelfLife
{
    /// <summary>Plasma for FFP/cryo and platelets must be separated within 8 hours of collection to keep labile factors.</summary>
    public static readonly TimeSpan SeparationWindow = TimeSpan.FromHours(8);

    public static int Days(BloodComponentType component) => component switch
    {
        BloodComponentType.WholeBlood => 35,
        BloodComponentType.PRBC => 35,
        BloodComponentType.FFP => 365,
        BloodComponentType.PlateletConcentrate => 5,
        _ => 365,
    };

    public static DateTimeOffset ExpiryFor(BloodComponentType component, DateTimeOffset collectedAt) => collectedAt.AddDays(Days(component));

    public static bool NeedsPromptSeparation(BloodComponentType component) =>
        component is BloodComponentType.FFP or BloodComponentType.PlateletConcentrate or BloodComponentType.Cryoprecipitate;

    /// <summary>Bag-number suffix printed on component labels.</summary>
    public static string Suffix(BloodComponentType component) => component switch
    {
        BloodComponentType.WholeBlood => "W",
        BloodComponentType.PRBC => "R",
        BloodComponentType.FFP => "F",
        BloodComponentType.PlateletConcentrate => "P",
        _ => "C",
    };

    /// <summary>Typical volume when the technician does not weigh the bag.</summary>
    public static int DefaultVolumeMl(BloodComponentType component, int donationVolumeMl) => component switch
    {
        BloodComponentType.WholeBlood => donationVolumeMl,
        BloodComponentType.PRBC => donationVolumeMl >= 450 ? 280 : 220,
        BloodComponentType.FFP => donationVolumeMl >= 450 ? 220 : 180,
        BloodComponentType.PlateletConcentrate => 60,
        _ => 20,
    };

    public static int MaxComponents(BloodBagType bag) => bag switch
    {
        BloodBagType.Single => 1,
        BloodBagType.Double => 2,
        BloodBagType.Triple => 3,
        _ => 4,
    };

    /// <summary>
    /// Validates a separation plan: whole blood is kept whole or split (never both), a split always yields PRBC,
    /// the number of components fits the bag's satellite bags, and plasma/platelets are made within 8 hours.
    /// </summary>
    public static Result ValidateSeparation(BloodBagType bag, IReadOnlyCollection<BloodComponentType> components, DateTimeOffset collectedAt, DateTimeOffset now)
    {
        if (components.Count == 0 || components.Distinct().Count() != components.Count)
        {
            return Error.Field("components", "Choose each component once.");
        }

        if (components.Contains(BloodComponentType.WholeBlood))
        {
            return components.Count == 1
                ? Result.Success()
                : Error.Rule("bloodbank.whole_blood_split", "Whole blood cannot be stored while also separating components from the same bag.");
        }

        if (bag == BloodBagType.Single)
        {
            return Error.Rule("bloodbank.single_bag", "A single bag has no satellite bags; keep it as whole blood.");
        }

        if (!components.Contains(BloodComponentType.PRBC))
        {
            return Error.Rule("bloodbank.prbc_required", "Separating a donation always yields packed red cells (PRBC).");
        }

        if (components.Count > MaxComponents(bag))
        {
            return Error.Rule("bloodbank.bag_too_small", $"A {bag.ToString().ToLowerInvariant()} bag gives at most {MaxComponents(bag)} component(s).");
        }

        return components.Any(NeedsPromptSeparation) && now - collectedAt > SeparationWindow
            ? Error.Rule("bloodbank.separation_too_late", "Plasma, platelets and cryoprecipitate must be separated within 8 hours of collection.")
            : Result.Success();
    }
}
