namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>Level of consciousness on the ACVPU scale (NEWS2): alert, new confusion, voice, pain, unresponsive.</summary>
public enum Consciousness
{
    Alert = 0,

    /// <summary>New confusion or disorientation (or worsening of it): scores 3, as V, P and U do.</summary>
    NewConfusion,
    Voice,
    Pain,
    Unresponsive,
}

/// <summary>
/// Which NEWS2 oxygen-saturation scale applies. Scale 2 is only for a patient in hypercapnic respiratory failure whose
/// doctor has prescribed a target saturation of 88–92 %; every other patient is scored on scale 1.
/// </summary>
public enum SpO2Scale
{
    Scale1 = 1,
    Scale2 = 2,
}

/// <summary>The clinical response NEWS2 calls for (Royal College of Physicians, NEWS2, 2017).</summary>
public enum News2Response
{
    /// <summary>Aggregate 0–4 and no single parameter scoring 3: routine ward monitoring.</summary>
    Routine = 0,

    /// <summary>Aggregate 5–6, or any single parameter scoring 3: urgent review by the ward's doctor.</summary>
    Urgent,

    /// <summary>Aggregate 7 or more: emergency response (critical-care outreach or the rapid response team).</summary>
    Emergency,
}

/// <summary>One parameter's part of the score, for the chart ("Respiration 26/min: 3").</summary>
public sealed record News2Part(string Parameter, string Value, int Points);

/// <param name="Total">The aggregate of the parameters that were recorded.</param>
/// <param name="IsComplete">
/// All seven parameters were recorded. A score from fewer can only rise when the rest are taken, so its response is
/// still acted on, but the chart marks it incomplete.
/// </param>
/// <param name="Missing">The parameters not recorded, in words.</param>
public sealed record News2Score(
    int Total,
    bool AnySingleParameterThree,
    News2Response Response,
    bool IsComplete,
    SpO2Scale Scale,
    IReadOnlyList<News2Part> Parts,
    IReadOnlyList<string> Missing)
{
    /// <summary>"NEWS2 6" or "NEWS2 3 (respiration 3)", for notifications and the bed board.</summary>
    public string Summary
    {
        get
        {
            var threes = Parts.Where(p => p.Points == 3).Select(p => p.Parameter.ToLowerInvariant()).ToList();
            var text = $"NEWS2 {Total}" + (threes.Count > 0 && Total < 7 ? $" ({string.Join(", ", threes)} scored 3)" : string.Empty);
            return IsComplete ? text : text + " — incomplete";
        }
    }
}

/// <summary>
/// The National Early Warning Score 2 (Royal College of Physicians, 2017) from one set of vital signs: respiration rate,
/// oxygen saturation (scale 1, or scale 2 when prescribed), air or supplemental oxygen, systolic blood pressure, pulse,
/// consciousness (ACVPU) and temperature. NEWS2 is validated for adults aged 16 and over who are not pregnant; for a
/// child it is not calculated (a paediatric score, PEWS, is not part of this release). Pure: the bands are the chart's.
/// </summary>
public static class News2
{
    /// <summary>NEWS2 is not used under this age.</summary>
    public const int MinimumAgeYears = 16;

    /// <summary>
    /// The score for <paramref name="reading"/>, or null when none of its parameters was recorded or the patient is younger
    /// than <see cref="MinimumAgeYears"/>. An unknown date of birth is scored as an adult, as the vital-signs flags are.
    /// </summary>
    public static News2Score? Score(News2Reading reading, DateOnly? dateOfBirth, DateOnly today)
    {
        if (dateOfBirth is { } dob && Age.InYears(dob, today) < MinimumAgeYears)
        {
            return null;
        }

        var parts = new List<News2Part>();
        var missing = new List<string>();

        if (reading.RespiratoryRate is { } rr)
        {
            parts.Add(new News2Part("Respiration", $"{rr}/min", rr switch
            {
                <= 8 => 3,
                <= 11 => 1,
                <= 20 => 0,
                <= 24 => 2,
                _ => 3,
            }));
        }
        else
        {
            missing.Add("respiration rate");
        }

        if (reading.SpO2 is { } spo2)
        {
            parts.Add(new News2Part(reading.Scale == SpO2Scale.Scale2 ? "SpO₂ (scale 2)" : "SpO₂", $"{spo2}%",
                reading.Scale == SpO2Scale.Scale2 ? Scale2Points(spo2, reading.OnSupplementalOxygen) : Scale1Points(spo2)));
        }
        else
        {
            missing.Add("oxygen saturation");
        }

        // Air or oxygen is always known: the form records oxygen as a tick, air when it is not ticked.
        parts.Add(new News2Part("Oxygen", reading.OnSupplementalOxygen ? "Supplemental oxygen" : "Air", reading.OnSupplementalOxygen ? 2 : 0));

        if (reading.SystolicBp is { } systolic)
        {
            parts.Add(new News2Part("Systolic BP", $"{systolic} mmHg", systolic switch
            {
                <= 90 => 3,
                <= 100 => 2,
                <= 110 => 1,
                <= 219 => 0,
                _ => 3,
            }));
        }
        else
        {
            missing.Add("systolic blood pressure");
        }

        if (reading.Pulse is { } pulse)
        {
            parts.Add(new News2Part("Pulse", $"{pulse}/min", pulse switch
            {
                <= 40 => 3,
                <= 50 => 1,
                <= 90 => 0,
                <= 110 => 1,
                <= 130 => 2,
                _ => 3,
            }));
        }
        else
        {
            missing.Add("pulse");
        }

        if (reading.Consciousness is { } level)
        {
            parts.Add(new News2Part("Consciousness", Describe(level), level == Consciousness.Alert ? 0 : 3));
        }
        else
        {
            missing.Add("consciousness (ACVPU)");
        }

        if (reading.TemperatureCelsius is { } temperature)
        {
            parts.Add(new News2Part("Temperature", $"{temperature:0.0} °C", temperature switch
            {
                <= 35.0m => 3,
                <= 36.0m => 1,
                <= 38.0m => 0,
                <= 39.0m => 1,
                _ => 2,
            }));
        }
        else
        {
            missing.Add("temperature");
        }

        // Only the oxygen tick: nothing was measured, so there is no score.
        if (missing.Count == 6)
        {
            return null;
        }

        var total = parts.Sum(p => p.Points);
        var anyThree = parts.Any(p => p.Points == 3);
        return new News2Score(total, anyThree, ResponseFor(total, anyThree), missing.Count == 0, reading.Scale, parts, missing);
    }

    /// <summary>7+ emergency; 5–6 or any single 3 urgent; otherwise routine.</summary>
    public static News2Response ResponseFor(int total, bool anySingleParameterThree) =>
        total >= 7 ? News2Response.Emergency
        : total >= 5 || anySingleParameterThree ? News2Response.Urgent
        : News2Response.Routine;

    public static string Describe(Consciousness level) => level switch
    {
        Consciousness.Alert => "Alert",
        Consciousness.NewConfusion => "New confusion",
        Consciousness.Voice => "Responds to voice",
        Consciousness.Pain => "Responds to pain",
        _ => "Unresponsive",
    };

    private static int Scale1Points(int spo2) => spo2 switch
    {
        <= 91 => 3,
        <= 93 => 2,
        <= 95 => 1,
        _ => 0,
    };

    /// <summary>
    /// Scale 2: 88–92 % scores 0 whatever the oxygen; above 92 % scores 0 on air but 1–3 on oxygen (over-oxygenation of a
    /// patient who retains carbon dioxide); below 88 % scores as on the chart.
    /// </summary>
    private static int Scale2Points(int spo2, bool onOxygen) => spo2 switch
    {
        <= 83 => 3,
        <= 85 => 2,
        <= 87 => 1,
        <= 92 => 0,
        _ when !onOxygen => 0,
        <= 94 => 1,
        <= 96 => 2,
        _ => 3,
    };
}

/// <summary>The parameters NEWS2 reads, temperature in °C.</summary>
public sealed record News2Reading(
    int? RespiratoryRate,
    int? SpO2,
    SpO2Scale Scale,
    bool OnSupplementalOxygen,
    int? SystolicBp,
    int? Pulse,
    Consciousness? Consciousness,
    decimal? TemperatureCelsius);
