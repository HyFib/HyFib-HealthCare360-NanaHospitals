using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// The modified Aldrete score, sign by sign, against the bands Aldrete published in 1995 (J Clin Anesth 1995;7:89–91):
/// activity, respiration, circulation (within 20% / 20–49% / 50% or more of the pre-anaesthetic pressure), consciousness
/// and oxygen saturation (above 92% on air / above 90% with oxygen / 90% or less), each 0 to 2, total 0 to 10.
/// </summary>
public sealed class AldreteScoreTests
{
    [Theory]
    [InlineData(4, 2)]
    [InlineData(3, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void Activity_is_scored_on_the_limbs_moved_on_command(int limbs, int points) =>
        AldreteScore.Activity(limbs).ShouldBe(points);

    [Theory]
    [InlineData(RecoveryBreathing.DeepBreathAndCough, 2)]
    [InlineData(RecoveryBreathing.Limited, 1)]
    [InlineData(RecoveryBreathing.Apnoeic, 0)]
    public void Respiration_is_scored_on_how_the_patient_breathes(RecoveryBreathing breathing, int points) =>
        AldreteScore.Respiration(breathing).ShouldBe(points);

    [Theory]
    [InlineData(120, 120, 2)]
    [InlineData(144, 120, 2)] // +20% exactly: within 20%
    [InlineData(96, 120, 2)] // -20% exactly
    [InlineData(145, 120, 1)] // just over 20%
    [InlineData(95, 120, 1)]
    [InlineData(179, 120, 1)] // +49%
    [InlineData(180, 120, 0)] // +50%
    [InlineData(60, 120, 0)] // -50%
    public void Circulation_is_the_systolic_against_the_pressure_before_anaesthesia(int systolic, int before, int points) =>
        AldreteScore.Circulation(systolic, before).ShouldBe(points);

    [Theory]
    [InlineData(RecoveryConsciousness.Alert, 2)]
    [InlineData(RecoveryConsciousness.Voice, 1)]
    [InlineData(RecoveryConsciousness.Pain, 0)]
    [InlineData(RecoveryConsciousness.Unresponsive, 0)]
    public void Consciousness_is_fully_awake_arousable_on_calling_or_not_responding(RecoveryConsciousness level, int points) =>
        AldreteScore.Consciousness(level).ShouldBe(points);

    [Theory]
    [InlineData(93, false, 2)]
    [InlineData(99, true, 1)] // needs oxygen to keep it up
    [InlineData(92, false, 1)] // not above 92% on air
    [InlineData(91, true, 1)]
    [InlineData(90, true, 0)]
    [InlineData(88, false, 0)]
    public void Oxygen_saturation_is_scored_on_air_or_with_oxygen(int spO2, bool onOxygen, int points) =>
        AldreteScore.OxygenSaturation(spO2, onOxygen).ShouldBe(points);

    [Fact]
    public void A_patient_awake_on_air_with_a_steady_pressure_scores_ten()
    {
        var score = AldreteScore.Score(Observation(), preAnaesthesiaSystolic: 124);

        score.Total.ShouldBe(10);
        score.MeetsWardDischarge.ShouldBeTrue();
        score.Missing.ShouldBeEmpty();
    }

    [Fact]
    public void Nine_is_enough_for_the_ward_and_eight_is_not()
    {
        AldreteScore.Score(Observation() with { SpO2 = 97, OnOxygen = true }, 124).Total.ShouldBe(9);
        AldreteScore.Score(Observation() with { SpO2 = 97, OnOxygen = true }, 124).MeetsWardDischarge.ShouldBeTrue();

        var eight = AldreteScore.Score(Observation() with { SpO2 = 97, OnOxygen = true, Consciousness = RecoveryConsciousness.Voice }, 124);
        eight.Total.ShouldBe(8);
        eight.MeetsWardDischarge.ShouldBeFalse();
    }

    [Fact]
    public void What_is_not_recorded_is_named_and_there_is_no_total()
    {
        var score = AldreteScore.Score(Observation() with { LimbsMoving = null, Breathing = null }, preAnaesthesiaSystolic: null);

        score.Total.ShouldBeNull();
        score.MeetsWardDischarge.ShouldBeFalse();
        score.Missing.ShouldBe(["how many limbs the patient moves on command", "how the patient is breathing", "the pre-anaesthetic blood pressure"]);
        score.Components.Single(c => c.Sign == AldreteSign.Consciousness).Points.ShouldBe(2);
    }

    [Fact]
    public void No_observations_score_nothing()
    {
        var score = AldreteScore.Score(null, 120);

        score.At.ShouldBeNull();
        score.Missing.Count.ShouldBe(5);
    }

    internal static RecoveryObservation Observation(DateTimeOffset? at = null) => new(
        Guid.CreateVersion7(), at ?? TestData.Now, HeartRate: 78, Systolic: 118, Diastolic: 72, SpO2: 96, OnOxygen: false, RespiratoryRate: 14,
        TemperatureC: 36.6m, RecoveryConsciousness.Alert, RecoveryBreathing.DeepBreathAndCough, LimbsMoving: 4, PainScore: 3, Ponv.None, null, "Sr. Leela Thomas");
}
