using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class ThresholdEvaluatorTests
{
    private static readonly ThresholdRange Systolic = new(90m, 140m, 80m, 180m);
    private static readonly ThresholdRange SpO2 = new(94m, null, 90m, null);

    [Theory]
    [InlineData(120, ReadingSeverity.Normal)]
    [InlineData(140, ReadingSeverity.Normal)]
    [InlineData(141, ReadingSeverity.Warning)]
    [InlineData(180, ReadingSeverity.Warning)]
    [InlineData(181, ReadingSeverity.Critical)]
    [InlineData(90, ReadingSeverity.Normal)]
    [InlineData(89, ReadingSeverity.Warning)]
    [InlineData(80, ReadingSeverity.Warning)]
    [InlineData(79, ReadingSeverity.Critical)]
    public void Systolic_pressure_is_classified_at_every_boundary(int value, ReadingSeverity expected) =>
        ThresholdEvaluator.Evaluate(value, Systolic).ShouldBe(expected);

    [Theory]
    [InlineData(100, ReadingSeverity.Normal)]
    [InlineData(94, ReadingSeverity.Normal)]
    [InlineData(93, ReadingSeverity.Warning)]
    [InlineData(90, ReadingSeverity.Warning)]
    [InlineData(89, ReadingSeverity.Critical)]
    public void Missing_bounds_are_ignored(int value, ReadingSeverity expected) =>
        ThresholdEvaluator.Evaluate(value, SpO2).ShouldBe(expected);

    [Fact]
    public void Decimal_values_are_compared_exactly()
    {
        var temperature = new ThresholdRange(36.1m, 37.8m, 35.0m, 39.0m);

        ThresholdEvaluator.Evaluate(37.8m, temperature).ShouldBe(ReadingSeverity.Normal);
        ThresholdEvaluator.Evaluate(37.81m, temperature).ShouldBe(ReadingSeverity.Warning);
        ThresholdEvaluator.Evaluate(39.01m, temperature).ShouldBe(ReadingSeverity.Critical);
    }

    [Fact]
    public void Without_limits_every_reading_is_normal() =>
        ThresholdEvaluator.Evaluate(300m, null).ShouldBe(ReadingSeverity.Normal);

    [Fact]
    public void Ecg_rhythm_classes_map_to_severity()
    {
        var rhythm = DefaultThresholds.For(RpmProgramKind.Cardiac)[RpmReadingType.EcgRhythm];

        ThresholdEvaluator.Evaluate(0m, rhythm).ShouldBe(ReadingSeverity.Normal);
        ThresholdEvaluator.Evaluate(1m, rhythm).ShouldBe(ReadingSeverity.Warning);
        ThresholdEvaluator.Evaluate(2m, rhythm).ShouldBe(ReadingSeverity.Critical);
    }

    [Theory]
    [InlineData(RpmProgramKind.Hypertension, RpmReadingType.SystolicBp)]
    [InlineData(RpmProgramKind.Diabetes, RpmReadingType.Glucose)]
    [InlineData(RpmProgramKind.Cardiac, RpmReadingType.SpO2)]
    [InlineData(RpmProgramKind.PostDischarge, RpmReadingType.Temperature)]
    public void Every_programme_starts_with_valid_default_limits(RpmProgramKind kind, RpmReadingType expectedType)
    {
        var defaults = DefaultThresholds.For(kind);

        defaults.ShouldContainKey(expectedType);
        defaults.Values.ShouldAllBe(range => range.Validate().IsSuccess);
    }

    [Fact]
    public void Custom_programmes_start_without_limits() => DefaultThresholds.For(RpmProgramKind.Custom).ShouldBeEmpty();

    [Fact]
    public void Limits_must_be_ordered()
    {
        new ThresholdRange(90m, 140m, 80m, 180m).Validate().IsSuccess.ShouldBeTrue();
        new ThresholdRange(140m, 90m).Validate().Error!.Fields!.ShouldContainKey("max");
        new ThresholdRange(90m, 140m, 95m, 180m).Validate().IsFailure.ShouldBeTrue();
        new ThresholdRange(90m, 140m, 80m, 130m).Validate().IsFailure.ShouldBeTrue();
        new ThresholdRange(null, null).Validate().Error!.Fields!.ShouldContainKey("min");
    }
}
