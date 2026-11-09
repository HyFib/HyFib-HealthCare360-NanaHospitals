namespace HyFib.HealthCare360.Modules.Quality.Domain.Hai;

/// <summary>The hospital-acquired infections HC360 surveys.</summary>
public enum HaiType
{
    /// <summary>Central line-associated bloodstream infection (NHSN LCBI with a central line).</summary>
    Clabsi = 0,

    /// <summary>Catheter-associated urinary tract infection (NHSN SUTI with an indwelling urinary catheter).</summary>
    Cauti,

    /// <summary>Ventilator-associated pneumonia (NHSN PNU1, clinically defined pneumonia, with a ventilator).</summary>
    Vap,

    /// <summary>Surgical site infection: superficial incisional, deep incisional or organ/space.</summary>
    Ssi,
}

/// <summary>One line of a case-review checklist.</summary>
/// <param name="Group">Lines in the same group are alternatives the definition counts together.</param>
public sealed record HaiCriterion(string Key, string Group, string Text);

/// <summary>What the reviewer ticked, and the facts HC360 works out itself.</summary>
/// <param name="DeviceEligible">CLABSI, CAUTI, VAP: the device in place more than two consecutive days on the date of event, and on it or the day before.</param>
/// <param name="CatheterInPlaceOnEventDay">CAUTI: urgency, frequency and dysuria count only once the catheter is out.</param>
/// <param name="DaysAfterOperation">SSI: days from the operation to the date of event (the day of the operation is day 1).</param>
public sealed record HaiEvidence(
    IReadOnlySet<string> Met,
    bool DeviceEligible,
    bool CatheterInPlaceOnEventDay = true,
    int? DaysAfterOperation = null);

/// <summary>Whether the case meets the surveillance definition and, when it does not, what is missing.</summary>
public sealed record HaiEvaluation(bool Meets, IReadOnlyList<string> Missing);

/// <summary>
/// The case-review checklists, from the CDC/NHSN Patient Safety Component Manual's surveillance definitions (BSI/LCBI,
/// UTI/SUTI, PNEU/PNU1 and SSI), as NABH asks hospitals to survey HAIs. The reviewer ticks what the record shows; the
/// device's days and the days since the operation come from HC360. Simplified on purpose where NHSN goes further: LCBI 3
/// (infants), asymptomatic bacteraemic UTI, PNU2/PNU3 and the ventilator-associated event (VAE) tiers, and the
/// organ/space site-specific criteria (ticked as one line) are not separate checklists.
/// </summary>
public static class HaiCriteria
{
    /// <summary>NHSN surveillance window for most operations; 90 days for those with an implant in the NHSN list.</summary>
    public const int SsiWindowDays = 30;

    public const int SsiImplantWindowDays = 90;

    public static IReadOnlyList<HaiCriterion> For(HaiType type) => type switch
    {
        HaiType.Clabsi => Clabsi,
        HaiType.Cauti => Cauti,
        HaiType.Vap => Vap,
        _ => Ssi,
    };

    private static readonly HaiCriterion[] Clabsi =
    [
        new("lcbi1-pathogen", "organism", "LCBI 1: a recognised pathogen identified from one or more blood specimens"),
        new("lcbi2-commensal", "organism", "LCBI 2: the same common commensal identified from two or more blood specimens collected on separate occasions"),
        new("lcbi2-symptom", "symptom", "LCBI 2: fever (> 38.0 °C), chills or hypotension"),
        new("not-secondary", "attribution", "The organism is not related to an infection at another site (not a secondary bloodstream infection)"),
    ];

    private static readonly HaiCriterion[] Cauti =
    [
        new("culture", "culture", "Urine culture with no more than two species, at least one of them ≥ 10⁵ CFU/ml"),
        new("fever", "sign", "Fever (> 38.0 °C)"),
        new("suprapubic", "sign", "Suprapubic tenderness with no other recognised cause"),
        new("cva", "sign", "Costovertebral angle pain or tenderness with no other recognised cause"),
        new("urgency", "after-removal", "Urinary urgency (counts only once the catheter is out)"),
        new("frequency", "after-removal", "Urinary frequency (counts only once the catheter is out)"),
        new("dysuria", "after-removal", "Dysuria (counts only once the catheter is out)"),
    ];

    private static readonly HaiCriterion[] Vap =
    [
        new("imaging", "imaging", "Two or more serial chest images with a new and persistent or progressive infiltrate, consolidation or cavitation (one image without underlying heart or lung disease)"),
        new("fever", "systemic", "Fever (> 38.0 °C)"),
        new("wbc", "systemic", "Leukopenia (< 4,000 WBC/mm³) or leukocytosis (≥ 12,000 WBC/mm³)"),
        new("mental", "systemic", "Altered mental status with no other recognised cause, aged 70 or over"),
        new("sputum", "pulmonary", "New purulent sputum, a change in its character, or more secretions or suctioning"),
        new("cough", "pulmonary", "New or worsening cough, dyspnoea or tachypnoea"),
        new("rales", "pulmonary", "Rales or bronchial breath sounds"),
        new("gas", "pulmonary", "Worsening gas exchange (desaturation, PaO₂/FiO₂ ≤ 240, more oxygen or ventilator demand)"),
    ];

    private static readonly HaiCriterion[] Ssi =
    [
        new("implant", "window", "An implant was placed (90-day surveillance window for deep and organ/space infections)"),
        new("superficial", "depth", "Superficial incisional: involves only the skin and subcutaneous tissue of the incision"),
        new("deep", "depth", "Deep incisional: involves the fascial and muscle layers"),
        new("organ-space", "depth", "Organ/space: involves any part of the body deeper than the fascia and muscle opened or manipulated"),
        new("purulent", "finding", "Purulent drainage from the incision (organ/space: from a drain placed into it)"),
        new("organism", "finding", "Organism identified from an aseptically obtained specimen of the incision, tissue or space"),
        new("opened", "finding", "Incision deliberately opened (deep: or dehisced) and culture positive or not cultured, with pain, tenderness, swelling, redness, heat or fever"),
        new("abscess", "finding", "An abscess or other evidence of infection found on examination, at re-operation, on imaging or on histology (deep and organ/space)"),
        new("diagnosis", "finding", "Superficial incisional SSI diagnosed by the surgeon or attending physician"),
        new("site-criterion", "site", "Organ/space: meets at least one criterion for a specific organ/space infection site"),
    ];

    /// <summary>Evaluates the ticked lines against the definition.</summary>
    public static HaiEvaluation Evaluate(HaiType type, HaiEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var missing = new List<string>();
        var met = evidence.Met;
        bool Has(string key) => met.Contains(key);

        switch (type)
        {
            case HaiType.Clabsi:
                Device(evidence, "central line", missing);
                if (!Has("lcbi1-pathogen") && !(Has("lcbi2-commensal") && Has("lcbi2-symptom")))
                {
                    missing.Add("A recognised pathogen (LCBI 1), or a common commensal from two blood specimens with fever, chills or hypotension (LCBI 2)");
                }

                if (!Has("not-secondary"))
                {
                    missing.Add("Confirmation that it is not secondary to an infection at another site");
                }

                break;

            case HaiType.Cauti:
                Device(evidence, "urinary catheter", missing);
                if (!Has("culture"))
                {
                    missing.Add("A urine culture with no more than two species, one ≥ 10⁵ CFU/ml");
                }

                var sign = Has("fever") || Has("suprapubic") || Has("cva")
                           || (!evidence.CatheterInPlaceOnEventDay && (Has("urgency") || Has("frequency") || Has("dysuria")));
                if (!sign)
                {
                    missing.Add(evidence.CatheterInPlaceOnEventDay
                        ? "A sign: fever, suprapubic tenderness or costovertebral angle pain (urgency, frequency and dysuria do not count while the catheter is in)"
                        : "A sign: fever, suprapubic tenderness, costovertebral angle pain, urgency, frequency or dysuria");
                }

                break;

            case HaiType.Vap:
                Device(evidence, "ventilator", missing);
                if (!Has("imaging"))
                {
                    missing.Add("Chest imaging with a new and persistent or progressive infiltrate, consolidation or cavitation");
                }

                if (!(Has("fever") || Has("wbc") || Has("mental")))
                {
                    missing.Add("A systemic sign: fever, leukopenia or leukocytosis, or altered mental status at 70 or over");
                }

                if (new[] { "sputum", "cough", "rales", "gas" }.Count(Has) < 2)
                {
                    missing.Add("At least two pulmonary signs (sputum, cough or dyspnoea, rales, gas exchange)");
                }

                break;

            default:
                EvaluateSsi(evidence, missing);
                break;
        }

        return new HaiEvaluation(missing.Count == 0, missing);
    }

    private static void EvaluateSsi(HaiEvidence evidence, List<string> missing)
    {
        var met = evidence.Met;
        var depths = new[] { "superficial", "deep", "organ-space" }.Where(met.Contains).ToList();
        if (depths.Count != 1)
        {
            missing.Add("Exactly one depth: superficial incisional, deep incisional or organ/space");
            return;
        }

        var depth = depths[0];
        var window = depth != "superficial" && met.Contains("implant") ? SsiImplantWindowDays : SsiWindowDays;
        if (evidence.DaysAfterOperation is not { } days)
        {
            missing.Add("The operation the infection follows");
        }
        else if (days < 1 || days > window)
        {
            missing.Add($"A date of event within {window} days of the operation (this is day {days})");
        }

        var found = depth switch
        {
            "superficial" => met.Contains("purulent") || met.Contains("organism") || met.Contains("opened") || met.Contains("diagnosis"),
            "deep" => met.Contains("purulent") || met.Contains("opened") || met.Contains("abscess"),
            _ => met.Contains("purulent") || met.Contains("organism") || met.Contains("abscess"),
        };
        if (!found)
        {
            missing.Add(depth switch
            {
                "superficial" => "A finding: purulent drainage, an organism from the incision, the incision opened with signs, or a doctor's diagnosis",
                "deep" => "A finding: purulent drainage from the deep incision, dehiscence or deliberate opening with signs, or an abscess",
                _ => "A finding: purulent drainage from a drain, an organism from the space, or an abscess",
            });
        }

        if (depth == "organ-space" && !met.Contains("site-criterion"))
        {
            missing.Add("A criterion for the specific organ/space infection site");
        }
    }

    private static void Device(HaiEvidence evidence, string device, List<string> missing)
    {
        if (!evidence.DeviceEligible)
        {
            missing.Add($"A {device} in place more than two consecutive days on the date of event, and on it or the day before");
        }
    }

    /// <summary>The device a type of infection is associated with; none for an SSI.</summary>
    public static InvasiveDevice? DeviceFor(HaiType type) => type switch
    {
        HaiType.Clabsi => InvasiveDevice.CentralLine,
        HaiType.Cauti => InvasiveDevice.UrinaryCatheter,
        HaiType.Vap => InvasiveDevice.Ventilator,
        _ => null,
    };
}
