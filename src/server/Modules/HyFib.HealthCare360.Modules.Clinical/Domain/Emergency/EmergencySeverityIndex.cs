namespace HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;

/// <summary>What the triage nurse answers at the ESI decision points (A, B and C); the vitals are measured.</summary>
/// <param name="NeedsLifeSavingIntervention">A: airway, breathing or circulation needs an intervention now, or the patient is unresponsive.</param>
/// <param name="HighRiskSituation">B: a situation that should not wait (chest pain of possible ACS, stroke signs, suicidal intent…).</param>
/// <param name="ConfusedLethargicOrDisoriented">B: new confusion, lethargy or disorientation.</param>
/// <param name="SevereDistress">B: severe pain or distress the nurse sees, whatever the pain score.</param>
/// <param name="ExpectedResources">C: how many different resources the ER will use (labs, imaging, IV fluids, IV/IM drugs, a specialist, a procedure): 0, 1, or 2 for "many".</param>
public sealed record EsiAnswers(
    bool NeedsLifeSavingIntervention,
    bool HighRiskSituation,
    bool ConfusedLethargicOrDisoriented,
    bool SevereDistress,
    int ExpectedResources);

/// <summary>The triage vitals, as measured. Any may be missing.</summary>
public sealed record TriageVitals(int? Pulse, int? RespiratoryRate, int? SpO2, int? SystolicBp, int? DiastolicBp, decimal? TemperatureCelsius, int? Gcs, int? PainScore);

/// <summary>The level the algorithm gives and why, decision point by decision point.</summary>
public sealed record EsiAssessment(int Level, IReadOnlyList<string> Reasons, bool DangerZoneVitals);

/// <summary>
/// The Emergency Severity Index, version 4: five levels from 1 (resuscitate now) to 5 (no resources needed), reached by
/// four decision points. A: needs an immediate life-saving intervention → 1. B: a high-risk situation, new confusion or
/// severe pain or distress → 2. C: the resources the ER will use, none → 5, one → 4, many → D. D: vitals in the danger zone
/// for the age → consider 2, else 3. The measured vitals also feed A and B: a GCS of 8 or less is an unresponsive patient,
/// a GCS below 15 an altered one, a pain score of 7 or more severe pain, and a fever in a baby under three months high
/// risk.
/// </summary>
public static class EmergencySeverityIndex
{
    /// <summary>A pulse or breathing rate above these, for the age in days, is in the danger zone (ESI v4, decision point D).</summary>
    private static readonly (int UpToDays, int Pulse, int Respiration)[] DangerZone =
    [
        (90, 180, 50),
        (3 * 365, 160, 40),
        (8 * 365, 140, 30),
        (int.MaxValue, 100, 20),
    ];

    public const int DangerZoneSpO2 = 92;

    public static EsiAssessment Assess(EsiAnswers answers, TriageVitals vitals, int? ageInDays)
    {
        var reasons = new List<string>();
        var danger = DangerZoneReasons(vitals, ageInDays);

        // A: life-saving intervention.
        if (answers.NeedsLifeSavingIntervention)
        {
            reasons.Add("A: needs an immediate life-saving intervention.");
        }

        if (vitals.Gcs is <= 8)
        {
            reasons.Add($"A: GCS {vitals.Gcs}, unresponsive.");
        }

        if (reasons.Count > 0)
        {
            return new EsiAssessment(1, reasons, danger.Count > 0);
        }

        // B: should not wait.
        if (answers.HighRiskSituation)
        {
            reasons.Add("B: high-risk situation.");
        }

        if (answers.ConfusedLethargicOrDisoriented)
        {
            reasons.Add("B: confused, lethargic or disoriented.");
        }
        else if (vitals.Gcs is < 15)
        {
            reasons.Add($"B: GCS {vitals.Gcs}, altered mental status.");
        }

        if (answers.SevereDistress)
        {
            reasons.Add("B: severe pain or distress.");
        }
        else if (vitals.PainScore is >= 7)
        {
            reasons.Add($"B: pain {vitals.PainScore}/10, severe.");
        }

        if (ageInDays is < 90 && vitals.TemperatureCelsius is >= 38.0m)
        {
            reasons.Add("B: fever of 38 °C or more in a baby under three months.");
        }

        if (reasons.Count > 0)
        {
            return new EsiAssessment(2, reasons, danger.Count > 0);
        }

        // C: resources.
        switch (answers.ExpectedResources)
        {
            case <= 0:
                return new EsiAssessment(5, ["C: no resources expected."], danger.Count > 0);
            case 1:
                return new EsiAssessment(4, ["C: one resource expected."], danger.Count > 0);
        }

        // D: danger-zone vitals.
        if (danger.Count > 0)
        {
            reasons.Add("C: many resources expected.");
            reasons.AddRange(danger.Select(d => $"D: {d}, in the danger zone; consider level 2."));
            return new EsiAssessment(2, reasons, true);
        }

        return new EsiAssessment(3, ["C: many resources expected.", "D: vitals outside the danger zone."], false);
    }

    /// <summary>The vitals in the danger zone for the age, described; unknown age counts as an adult.</summary>
    public static IReadOnlyList<string> DangerZoneReasons(TriageVitals vitals, int? ageInDays)
    {
        var band = DangerZone.First(b => (ageInDays ?? int.MaxValue) <= b.UpToDays);
        var reasons = new List<string>();
        if (vitals.Pulse > band.Pulse)
        {
            reasons.Add($"pulse {vitals.Pulse}/min (above {band.Pulse})");
        }

        if (vitals.RespiratoryRate > band.Respiration)
        {
            reasons.Add($"breathing {vitals.RespiratoryRate}/min (above {band.Respiration})");
        }

        if (vitals.SpO2 < DangerZoneSpO2)
        {
            reasons.Add($"SpO2 {vitals.SpO2}% (below {DangerZoneSpO2})");
        }

        return reasons;
    }
}
