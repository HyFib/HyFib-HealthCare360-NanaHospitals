namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public sealed record RiskScore(int Score, RiskLevel Risk, string Interpretation);

/// <summary>Morse item 3: what the patient uses to walk.</summary>
public enum AmbulatoryAid
{
    /// <summary>None, bed rest or nurse assisted (0).</summary>
    None = 0,

    /// <summary>Crutches, cane or walker (15).</summary>
    CrutchesCaneWalker,

    /// <summary>Clutches furniture to walk (30).</summary>
    Furniture,
}

/// <summary>Morse item 5: gait.</summary>
public enum GaitType
{
    /// <summary>Normal, bed rest or wheelchair (0).</summary>
    Normal = 0,

    /// <summary>Weak (10).</summary>
    Weak,

    /// <summary>Impaired (20).</summary>
    Impaired,
}

public sealed record MorseFallInput(
    bool HistoryOfFalling,
    bool SecondaryDiagnosis,
    AmbulatoryAid AmbulatoryAid,
    bool IntravenousAccess,
    GaitType Gait,
    bool OverestimatesAbility);

/// <summary>
/// Morse Fall Scale (Morse 1989): history of falling 25, secondary diagnosis 15, ambulatory aid 0/15/30, IV access or
/// heparin lock 20, gait 0/10/20, forgets own limitations 15 (0–125). Bands follow the widely used hospital cut-offs:
/// 0–24 low, 25–44 moderate, 45 and above high risk.
/// </summary>
public static class MorseFallScale
{
    public const int ModerateRiskFrom = 25;
    public const int HighRiskFrom = 45;

    public static RiskScore Calculate(MorseFallInput input)
    {
        var score = (input.HistoryOfFalling ? 25 : 0)
                    + (input.SecondaryDiagnosis ? 15 : 0)
                    + input.AmbulatoryAid switch { AmbulatoryAid.CrutchesCaneWalker => 15, AmbulatoryAid.Furniture => 30, _ => 0 }
                    + (input.IntravenousAccess ? 20 : 0)
                    + input.Gait switch { GaitType.Weak => 10, GaitType.Impaired => 20, _ => 0 }
                    + (input.OverestimatesAbility ? 15 : 0);

        return score >= HighRiskFrom ? new RiskScore(score, RiskLevel.High, "High fall risk: apply high-risk fall precautions and reassess every shift.")
            : score >= ModerateRiskFrom ? new RiskScore(score, RiskLevel.Moderate, "Moderate fall risk: apply standard fall-prevention measures.")
            : new RiskScore(score, RiskLevel.Low, "Low fall risk: good basic nursing care.");
    }
}

/// <summary>Braden subscale scores: 1–4 each, friction and shear 1–3.</summary>
public sealed record BradenInput(int SensoryPerception, int Moisture, int Activity, int Mobility, int Nutrition, int FrictionShear);

/// <summary>
/// Braden Scale for pressure-injury risk (6–23; lower is worse): 19–23 no risk, 15–18 mild, 13–14 moderate,
/// 10–12 high, 9 or less very high.
/// </summary>
public static class BradenScale
{
    public static RiskScore Calculate(BradenInput input)
    {
        CheckRange(input.SensoryPerception, 4, nameof(input.SensoryPerception));
        CheckRange(input.Moisture, 4, nameof(input.Moisture));
        CheckRange(input.Activity, 4, nameof(input.Activity));
        CheckRange(input.Mobility, 4, nameof(input.Mobility));
        CheckRange(input.Nutrition, 4, nameof(input.Nutrition));
        CheckRange(input.FrictionShear, 3, nameof(input.FrictionShear));

        var score = input.SensoryPerception + input.Moisture + input.Activity + input.Mobility + input.Nutrition + input.FrictionShear;
        return score switch
        {
            <= 9 => new RiskScore(score, RiskLevel.VeryHigh, "Very high pressure-injury risk: two-hourly repositioning, pressure-redistribution mattress, skin checks every shift."),
            <= 12 => new RiskScore(score, RiskLevel.High, "High pressure-injury risk: two-hourly repositioning and a pressure-relieving surface."),
            <= 14 => new RiskScore(score, RiskLevel.Moderate, "Moderate pressure-injury risk: turning schedule and heel protection."),
            <= 18 => new RiskScore(score, RiskLevel.Low, "Mild pressure-injury risk: encourage mobility and keep skin dry."),
            _ => new RiskScore(score, RiskLevel.None, "No significant pressure-injury risk."),
        };
    }

    private static void CheckRange(int value, int max, string name)
    {
        if (value < 1 || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Braden subscale scores range from 1 to {max}.");
        }
    }
}

public enum PainScaleKind
{
    /// <summary>Numeric rating scale 0–10 (adults who can self-report).</summary>
    Numeric = 0,

    /// <summary>Wong-Baker FACES 0–10 (children, low literacy).</summary>
    WongBakerFaces,

    /// <summary>FLACC 0–10 (infants and non-verbal patients).</summary>
    Flacc,
}

public sealed record PainInput(int Score, PainScaleKind Scale, string? Location, string? Character);

/// <summary>Pain score on a 0–10 scale: 0 none, 1–3 mild, 4–6 moderate, 7–10 severe (the "fifth vital sign").</summary>
public static class PainScale
{
    public static RiskScore Calculate(PainInput input)
    {
        if (input.Score is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.Score, "Pain scores range from 0 to 10.");
        }

        return input.Score switch
        {
            0 => new RiskScore(0, RiskLevel.None, "No pain."),
            <= 3 => new RiskScore(input.Score, RiskLevel.Low, "Mild pain: comfort measures; reassess next round."),
            <= 6 => new RiskScore(input.Score, RiskLevel.Moderate, "Moderate pain: give prescribed analgesia and reassess within an hour."),
            _ => new RiskScore(input.Score, RiskLevel.High, "Severe pain: inform the doctor and reassess within 30 minutes of treatment."),
        };
    }
}
