using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>NEWS2 (Royal College of Physicians, 2017): each parameter's bands, the response thresholds, scale 2, children.</summary>
public sealed class News2Tests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    private static News2Score Score(
        int? rr = 16, int? spo2 = 97, bool oxygen = false, int? sbp = 120, int? pulse = 75, Consciousness? level = Consciousness.Alert,
        decimal? temp = 37.0m, SpO2Scale scale = SpO2Scale.Scale1) =>
        News2.Score(new News2Reading(rr, spo2, scale, oxygen, sbp, pulse, level, temp), new DateOnly(1970, 1, 1), Today).ShouldNotBeNull();

    private static int Points(News2Score score, string parameter) => score.Parts.Single(p => p.Parameter.StartsWith(parameter)).Points;

    [Theory]
    [InlineData(8, 3)]
    [InlineData(9, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 0)]
    [InlineData(20, 0)]
    [InlineData(21, 2)]
    [InlineData(24, 2)]
    [InlineData(25, 3)]
    public void Respiration_rate_bands(int rr, int points) => Points(Score(rr: rr), "Respiration").ShouldBe(points);

    [Theory]
    [InlineData(91, 3)]
    [InlineData(92, 2)]
    [InlineData(93, 2)]
    [InlineData(94, 1)]
    [InlineData(95, 1)]
    [InlineData(96, 0)]
    public void Oxygen_saturation_scale_1_bands(int spo2, int points) => Points(Score(spo2: spo2), "SpO₂").ShouldBe(points);

    [Theory]
    [InlineData(83, false, 3)]
    [InlineData(84, false, 2)]
    [InlineData(86, false, 1)]
    [InlineData(88, true, 0)]
    [InlineData(92, true, 0)]
    [InlineData(97, false, 0)]
    [InlineData(93, true, 1)]
    [InlineData(95, true, 2)]
    [InlineData(97, true, 3)]
    public void Oxygen_saturation_scale_2_scores_over_oxygenation_only_on_oxygen(int spo2, bool oxygen, int points) =>
        Points(Score(spo2: spo2, oxygen: oxygen, scale: SpO2Scale.Scale2), "SpO₂").ShouldBe(points);

    [Theory]
    [InlineData(90, 3)]
    [InlineData(91, 2)]
    [InlineData(100, 2)]
    [InlineData(101, 1)]
    [InlineData(110, 1)]
    [InlineData(111, 0)]
    [InlineData(219, 0)]
    [InlineData(220, 3)]
    public void Systolic_pressure_bands(int sbp, int points) => Points(Score(sbp: sbp), "Systolic").ShouldBe(points);

    [Theory]
    [InlineData(40, 3)]
    [InlineData(41, 1)]
    [InlineData(50, 1)]
    [InlineData(51, 0)]
    [InlineData(90, 0)]
    [InlineData(91, 1)]
    [InlineData(110, 1)]
    [InlineData(111, 2)]
    [InlineData(130, 2)]
    [InlineData(131, 3)]
    public void Pulse_bands(int pulse, int points) => Points(Score(pulse: pulse), "Pulse").ShouldBe(points);

    [Theory]
    [InlineData("35.0", 3)]
    [InlineData("35.1", 1)]
    [InlineData("36.0", 1)]
    [InlineData("36.1", 0)]
    [InlineData("38.0", 0)]
    [InlineData("38.1", 1)]
    [InlineData("39.0", 1)]
    [InlineData("39.1", 2)]
    public void Temperature_bands(string temp, int points) =>
        Points(Score(temp: decimal.Parse(temp, System.Globalization.CultureInfo.InvariantCulture)), "Temperature").ShouldBe(points);

    [Theory]
    [InlineData(Consciousness.Alert, 0)]
    [InlineData(Consciousness.NewConfusion, 3)]
    [InlineData(Consciousness.Voice, 3)]
    [InlineData(Consciousness.Pain, 3)]
    [InlineData(Consciousness.Unresponsive, 3)]
    public void Consciousness_scores_3_for_anything_but_alert(Consciousness level, int points) =>
        Points(Score(level: level), "Consciousness").ShouldBe(points);

    [Fact]
    public void Supplemental_oxygen_scores_2() => Points(Score(oxygen: true), "Oxygen").ShouldBe(2);

    [Fact]
    public void Thresholds_follow_the_chart()
    {
        Score().ShouldSatisfyAllConditions(s => s.Total.ShouldBe(0), s => s.Response.ShouldBe(News2Response.Routine), s => s.IsComplete.ShouldBeTrue());

        // 4 with no single 3: routine. 5: urgent. A single 3 (systolic 88) with a total of 3: urgent. 7: emergency.
        Score(rr: 22, spo2: 95, pulse: 95).ShouldSatisfyAllConditions(s => s.Total.ShouldBe(4), s => s.Response.ShouldBe(News2Response.Routine));
        Score(rr: 22, spo2: 95, oxygen: true).ShouldSatisfyAllConditions(s => s.Total.ShouldBe(5), s => s.Response.ShouldBe(News2Response.Urgent));
        Score(sbp: 88).ShouldSatisfyAllConditions(s => s.Total.ShouldBe(3), s => s.AnySingleParameterThree.ShouldBeTrue(),
            s => s.Response.ShouldBe(News2Response.Urgent), s => s.Summary.ShouldBe("NEWS2 3 (systolic bp scored 3)"));
        Score(rr: 22, spo2: 95, oxygen: true, sbp: 105, pulse: 95).ShouldSatisfyAllConditions(
            s => s.Total.ShouldBe(7), s => s.Response.ShouldBe(News2Response.Emergency));
        News2.ResponseFor(6, false).ShouldBe(News2Response.Urgent);
        News2.ResponseFor(4, true).ShouldBe(News2Response.Urgent);
        News2.ResponseFor(7, false).ShouldBe(News2Response.Emergency);
    }

    [Fact]
    public void A_score_from_fewer_parameters_is_marked_incomplete_but_still_acted_on()
    {
        var partial = Score(rr: 26, spo2: null, sbp: null, pulse: 135, level: null, temp: null);
        partial.IsComplete.ShouldBeFalse();
        partial.Missing.ShouldBe(["oxygen saturation", "systolic blood pressure", "consciousness (ACVPU)", "temperature"]);
        (partial.Total, partial.Response).ShouldBe((6, News2Response.Urgent));
        partial.Summary.ShouldEndWith("incomplete");
    }

    [Fact]
    public void Nothing_measured_is_no_score_and_a_child_is_not_scored()
    {
        News2.Score(new News2Reading(null, null, SpO2Scale.Scale1, true, null, null, null, null), null, Today).ShouldBeNull();
        News2.Score(new News2Reading(30, 90, SpO2Scale.Scale1, false, 85, 140, Consciousness.Alert, 39m), Today.AddYears(-15), Today).ShouldBeNull();
        News2.Score(new News2Reading(16, 97, SpO2Scale.Scale1, false, 120, 75, Consciousness.Alert, 37m), Today.AddYears(-16), Today).ShouldNotBeNull();
        News2.Score(new News2Reading(16, 97, SpO2Scale.Scale1, false, 120, 75, Consciousness.Alert, 37m), null, Today).ShouldNotBeNull("an unknown age is scored as an adult");
    }
}
