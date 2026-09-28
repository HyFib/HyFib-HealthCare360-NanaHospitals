using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class RiskScaleTests
{
    private static MorseFallInput Morse(
        bool history = false, bool secondary = false, AmbulatoryAid aid = AmbulatoryAid.None, bool iv = false, GaitType gait = GaitType.Normal, bool forgets = false) =>
        new(history, secondary, aid, iv, gait, forgets);

    [Fact]
    public void Morse_without_risk_factors_is_low_risk()
    {
        var score = MorseFallScale.Calculate(Morse());

        score.Score.ShouldBe(0);
        score.Risk.ShouldBe(RiskLevel.Low);
    }

    [Fact]
    public void Morse_maximum_is_125_and_high_risk()
    {
        var score = MorseFallScale.Calculate(Morse(true, true, AmbulatoryAid.Furniture, true, GaitType.Impaired, true));

        score.Score.ShouldBe(125);
        score.Risk.ShouldBe(RiskLevel.High);
    }

    [Theory]
    [InlineData(false, false, AmbulatoryAid.None, true, GaitType.Normal, false, 20, RiskLevel.Low)]
    [InlineData(true, false, AmbulatoryAid.None, false, GaitType.Normal, false, 25, RiskLevel.Moderate)]
    [InlineData(true, true, AmbulatoryAid.None, false, GaitType.Normal, false, 40, RiskLevel.Moderate)]
    [InlineData(false, false, AmbulatoryAid.CrutchesCaneWalker, false, GaitType.Weak, true, 40, RiskLevel.Moderate)]
    [InlineData(true, false, AmbulatoryAid.None, true, GaitType.Normal, false, 45, RiskLevel.High)]
    [InlineData(false, true, AmbulatoryAid.Furniture, false, GaitType.Normal, false, 45, RiskLevel.High)]
    public void Morse_items_and_bands(bool history, bool secondary, AmbulatoryAid aid, bool iv, GaitType gait, bool forgets, int expected, RiskLevel risk)
    {
        var score = MorseFallScale.Calculate(new MorseFallInput(history, secondary, aid, iv, gait, forgets));

        score.Score.ShouldBe(expected);
        score.Risk.ShouldBe(risk);
    }

    [Fact]
    public void Braden_worst_scores_six_and_is_very_high_risk()
    {
        var score = BradenScale.Calculate(new BradenInput(1, 1, 1, 1, 1, 1));

        score.Score.ShouldBe(6);
        score.Risk.ShouldBe(RiskLevel.VeryHigh);
    }

    [Fact]
    public void Braden_best_scores_twenty_three_and_carries_no_risk()
    {
        var score = BradenScale.Calculate(new BradenInput(4, 4, 4, 4, 4, 3));

        score.Score.ShouldBe(23);
        score.Risk.ShouldBe(RiskLevel.None);
    }

    [Theory]
    [InlineData(9, RiskLevel.VeryHigh)]
    [InlineData(10, RiskLevel.High)]
    [InlineData(12, RiskLevel.High)]
    [InlineData(13, RiskLevel.Moderate)]
    [InlineData(14, RiskLevel.Moderate)]
    [InlineData(15, RiskLevel.Low)]
    [InlineData(18, RiskLevel.Low)]
    [InlineData(19, RiskLevel.None)]
    public void Braden_bands(int total, RiskLevel risk)
    {
        var score = BradenScale.Calculate(BradenTotalling(total));

        score.Score.ShouldBe(total);
        score.Risk.ShouldBe(risk);
    }

    [Theory]
    [InlineData(0, 2, 2, 2, 2, 2)]
    [InlineData(5, 2, 2, 2, 2, 2)]
    [InlineData(2, 2, 2, 2, 2, 4)]
    public void Braden_rejects_scores_outside_each_subscale(int sensory, int moisture, int activity, int mobility, int nutrition, int friction) =>
        Should.Throw<ArgumentOutOfRangeException>(() => BradenScale.Calculate(new BradenInput(sensory, moisture, activity, mobility, nutrition, friction)));

    [Theory]
    [InlineData(0, RiskLevel.None)]
    [InlineData(1, RiskLevel.Low)]
    [InlineData(3, RiskLevel.Low)]
    [InlineData(4, RiskLevel.Moderate)]
    [InlineData(6, RiskLevel.Moderate)]
    [InlineData(7, RiskLevel.High)]
    [InlineData(10, RiskLevel.High)]
    public void Pain_bands(int painScore, RiskLevel risk) =>
        PainScale.Calculate(new PainInput(painScore, PainScaleKind.Numeric, "Abdomen", "Colicky")).Risk.ShouldBe(risk);

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Pain_scores_outside_zero_to_ten_are_rejected(int painScore) =>
        Should.Throw<ArgumentOutOfRangeException>(() => PainScale.Calculate(new PainInput(painScore, PainScaleKind.Numeric, null, null)));

    /// <summary>Distributes a total over the six subscales, respecting each maximum (4, 4, 4, 4, 4, 3).</summary>
    private static BradenInput BradenTotalling(int total)
    {
        int[] maxima = [4, 4, 4, 4, 4, 3];
        var values = new[] { 1, 1, 1, 1, 1, 1 };
        var remaining = total - 6;
        for (var i = 0; i < values.Length && remaining > 0; i++)
        {
            var add = Math.Min(maxima[i] - values[i], remaining);
            values[i] += add;
            remaining -= add;
        }

        return new BradenInput(values[0], values[1], values[2], values[3], values[4], values[5]);
    }
}
