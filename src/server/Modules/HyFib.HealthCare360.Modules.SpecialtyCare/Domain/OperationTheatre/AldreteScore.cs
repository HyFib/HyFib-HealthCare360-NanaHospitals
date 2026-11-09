namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>The five signs of the modified Aldrete score, each scored 0, 1 or 2.</summary>
public enum AldreteSign
{
    Activity = 0,
    Respiration,
    Circulation,
    Consciousness,
    OxygenSaturation,
}

/// <summary>One sign as scored from the observations, or what is missing to score it.</summary>
public sealed record AldreteComponent(AldreteSign Sign, int? Points, string? Missing);

/// <summary>
/// The modified Aldrete score of one set of recovery observations: each sign, the total once all five can be scored, and
/// when the observations were taken.
/// </summary>
public sealed record AldreteResult(IReadOnlyList<AldreteComponent> Components, DateTimeOffset? At)
{
    public bool IsComplete => Components.All(c => c.Points is not null);

    /// <summary>The total out of 10, once every sign can be scored.</summary>
    public int? Total => IsComplete ? Components.Sum(c => c.Points!.Value) : null;

    /// <summary>What still has to be recorded before the score can be given.</summary>
    public IReadOnlyList<string> Missing => [.. Components.Where(c => c.Missing is not null).Select(c => c.Missing!)];

    /// <summary>9 or more out of 10: fit to go back to the ward on the score alone.</summary>
    public bool MeetsWardDischarge => Total >= AldreteScore.WardDischargeMinimum;
}

/// <summary>
/// The modified Aldrete post-anaesthesia recovery score (Aldrete JA, "The post-anesthesia recovery score revisited",
/// J Clin Anesth 1995;7:89–91, doi 10.1016/0952-8180(94)00001-k), which replaced the colour of the original 1970 score
/// with the oxygen saturation:
/// <list type="bullet">
/// <item>Activity: moves 4 extremities voluntarily or on command 2; 2 extremities 1; none 0.</item>
/// <item>Respiration: breathes deeply and coughs freely 2; dyspnoea or limited breathing 1; apnoeic 0.</item>
/// <item>Circulation: blood pressure within 20% of the pre-anaesthetic level 2; 20–49% 1; 50% or more 0.</item>
/// <item>Consciousness: fully awake 2; arousable on calling 1; not responding 0.</item>
/// <item>O₂ saturation: keeps SpO₂ above 92% on room air 2; needs oxygen to keep it above 90% 1; below 90% even with oxygen 0.</item>
/// </list>
/// A total of 9 or more is the usual criterion to leave recovery for the ward. Pure: no clock, no store.
/// </summary>
public static class AldreteScore
{
    /// <summary>The total that lets the patient go back to the ward without the anaesthetist's override.</summary>
    public const int WardDischargeMinimum = 9;

    /// <summary>Observations older than this do not say how the patient is at the discharge: they are taken again.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);

    /// <summary>Extremities moved on command (0–4): all four 2; two or three 1; fewer 0.</summary>
    public static int? Activity(int? limbsMoving) => limbsMoving switch
    {
        null => null,
        >= 4 => 2,
        >= 2 => 1,
        _ => 0,
    };

    public static int? Respiration(RecoveryBreathing? breathing) => breathing switch
    {
        RecoveryBreathing.DeepBreathAndCough => 2,
        RecoveryBreathing.Limited => 1,
        RecoveryBreathing.Apnoeic => 0,
        _ => null,
    };

    /// <summary>
    /// The systolic pressure against the pre-anaesthetic systolic: within 20% 2; more than 20% and under 50% 1; 50% or
    /// more 0. The systolic is what is charted on every row, before and after.
    /// </summary>
    public static int? Circulation(int? systolic, int? preAnaesthesiaSystolic)
    {
        if (systolic is not { } now || preAnaesthesiaSystolic is not { } before || before <= 0)
        {
            return null;
        }

        var change = Math.Abs(now - before) * 100m / before;
        return change <= 20m ? 2 : change < 50m ? 1 : 0;
    }

    /// <summary>AVPU: alert is fully awake; responding to voice is arousable on calling; to pain only, or not at all, is not responding.</summary>
    public static int? Consciousness(RecoveryConsciousness? level) => level switch
    {
        RecoveryConsciousness.Alert => 2,
        RecoveryConsciousness.Voice => 1,
        RecoveryConsciousness.Pain or RecoveryConsciousness.Unresponsive => 0,
        _ => null,
    };

    /// <summary>Above 92% on room air 2; above 90% (on oxygen, or on air short of 92%) 1; 90% or less 0.</summary>
    public static int? OxygenSaturation(int? spO2, bool onOxygen) => spO2 switch
    {
        null => null,
        > 92 when !onOxygen => 2,
        > 90 => 1,
        _ => 0,
    };

    /// <summary>The score of one set of observations; null observations score nothing and say what to record.</summary>
    public static AldreteResult Score(RecoveryObservation? observation, int? preAnaesthesiaSystolic)
    {
        var o = observation;
        return new AldreteResult(
        [
            Component(AldreteSign.Activity, Activity(o?.LimbsMoving), "how many limbs the patient moves on command"),
            Component(AldreteSign.Respiration, Respiration(o?.Breathing), "how the patient is breathing"),
            Component(AldreteSign.Circulation, Circulation(o?.Systolic, preAnaesthesiaSystolic),
                preAnaesthesiaSystolic is null ? "the pre-anaesthetic blood pressure" : "the blood pressure"),
            Component(AldreteSign.Consciousness, Consciousness(o?.Consciousness), "the level of consciousness"),
            Component(AldreteSign.OxygenSaturation, OxygenSaturation(o?.SpO2, o?.OnOxygen ?? false), "the SpO₂"),
        ], o?.At);

        static AldreteComponent Component(AldreteSign sign, int? points, string missing) => new(sign, points, points is null ? missing : null);
    }
}
