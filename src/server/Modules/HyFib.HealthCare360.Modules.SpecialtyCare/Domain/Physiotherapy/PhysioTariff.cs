namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;

public static class PhysioServiceCodes
{
    public const string Ift = "PHY-IFT";
    public const string Tens = "PHY-TENS";
    public const string Ust = "PHY-UST";
    public const string Exercise = "PHY-EXERCISE";

    /// <summary>General session charge covering modalities without a tariff line of their own (hot pack, traction…).</summary>
    public const string Session = "PHY-SESSION";
}

/// <summary>How an attended session is billed (PHY-04).</summary>
public static class PhysioTariff
{
    private static readonly IReadOnlyDictionary<PhysioModality, string> Dedicated = new Dictionary<PhysioModality, string>
    {
        [PhysioModality.IFT] = PhysioServiceCodes.Ift,
        [PhysioModality.TENS] = PhysioServiceCodes.Tens,
        [PhysioModality.UST] = PhysioServiceCodes.Ust,
        [PhysioModality.Exercises] = PhysioServiceCodes.Exercise,
    };

    /// <summary>
    /// One line per modality that has its own tariff, plus a single general session line when any other modality
    /// was given — matching how Indian physiotherapy departments bill per modality per sitting.
    /// </summary>
    public static IReadOnlyList<string> ChargeCodesFor(IReadOnlyCollection<PhysioModality> modalities)
    {
        var codes = modalities.Distinct()
            .Where(Dedicated.ContainsKey)
            .Select(m => Dedicated[m])
            .ToList();

        if (modalities.Count == 0 || modalities.Any(m => !Dedicated.ContainsKey(m)))
        {
            codes.Add(PhysioServiceCodes.Session);
        }

        return codes;
    }
}
