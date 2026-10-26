namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

/// <summary>
/// The essential narcotic drugs (ENDs) of the NDPS (Amendment) Rules 2015, which a recognised medical institution
/// stocks and accounts for under rules 52-O to 52-T: day-to-day accounts (Form 3H), each patient's record (Form 3E), the
/// annual estimate (Form 3J) and return. Recognised by the item's generic name, whatever the brand or strength.
/// </summary>
public static class EssentialNarcoticDrugs
{
    public static IReadOnlyList<string> GenericNames { get; } = ["Morphine", "Fentanyl", "Methadone", "Oxycodone", "Codeine", "Hydrocodone"];

    /// <summary>"Morphine sulphate", "Fentanyl citrate": the generic name begins with one of the ENDs.</summary>
    public static bool Is(string? genericName) =>
        !string.IsNullOrWhiteSpace(genericName)
        && GenericNames.Any(g => genericName.TrimStart().StartsWith(g, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The estimate of next year's requirement (Form 3J): this year's consumption with a margin, so a rise in patients
    /// does not run the stock out before a revised estimate is approved.
    /// </summary>
    public static decimal Estimate(decimal consumed, decimal marginPercent = 10m) =>
        consumed <= 0 ? 0 : Math.Ceiling(consumed * (100m + marginPercent) / 100m);
}

public enum NarcoticCountStatus
{
    /// <summary>Counted by one person, waiting for a second to witness it.</summary>
    Counted = 0,

    Witnessed,
}

/// <summary>
/// The count of a store's essential narcotic drugs at a change of shift, by two people. The first counts blind: the
/// register balance is not shown, and the server takes it at the moment of the count. A second member of staff, signed
/// in as themselves, witnesses it; a difference from the register must be explained before it is witnessed, and is
/// reported to the pharmacist in charge.
/// </summary>
public sealed class NarcoticCount : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid StoreId { get; set; }
    public DateTimeOffset CountedAt { get; set; }
    public Guid? CountedBy { get; set; }
    public string CountedByName { get; set; } = string.Empty;
    public NarcoticCountStatus Status { get; private set; }
    public DateTimeOffset? WitnessedAt { get; private set; }
    public Guid? WitnessedBy { get; private set; }
    public string? WitnessedByName { get; private set; }

    /// <summary>Why the count differs from the register, by whoever counted or witnessed it.</summary>
    public string? Explanation { get; set; }

    public List<NarcoticCountLine> Lines { get; set; } = [];

    public bool HasDifference => Lines.Any(l => l.Difference != 0);

    public Result Witness(Guid? userId, string userName, string? explanation, DateTimeOffset at)
    {
        if (Status == NarcoticCountStatus.Witnessed)
        {
            return Error.Rule("ndps.count_witnessed", "This count is already witnessed.");
        }

        if (userId is null || userId == CountedBy)
        {
            return Error.Rule("ndps.witness_self", "A second member of staff witnesses the count, signed in as themselves.");
        }

        if (!string.IsNullOrWhiteSpace(explanation))
        {
            Explanation = explanation.Trim();
        }

        if (HasDifference && string.IsNullOrWhiteSpace(Explanation))
        {
            return Error.Field("explanation", "The count differs from the register: explain the difference before witnessing it.");
        }

        Status = NarcoticCountStatus.Witnessed;
        WitnessedAt = at;
        WitnessedBy = userId;
        WitnessedByName = userName;
        return Result.Success();
    }
}

public sealed class NarcoticCountLine : Entity
{
    public Guid NarcoticCountId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;

    /// <summary>The register's balance of the drug in the store when it was counted.</summary>
    public decimal RegisterBalance { get; set; }

    public decimal Counted { get; set; }

    public decimal Difference => Counted - RegisterBalance;
}
