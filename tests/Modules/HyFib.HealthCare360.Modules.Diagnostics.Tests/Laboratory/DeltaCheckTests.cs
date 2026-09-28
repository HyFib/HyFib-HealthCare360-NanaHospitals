using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

public sealed class DeltaCheckTests
{
    private static readonly DateTimeOffset PreviousAt = TestData.Now.AddDays(-2);

    [Fact]
    public void Without_a_previous_result_there_is_nothing_to_compare()
    {
        DeltaCheck.Evaluate(4.2m, null, null, 20m).ShouldBe(DeltaOutcome.None);
        DeltaCheck.Evaluate(null, 4.0m, PreviousAt, 20m).ShouldBe(DeltaOutcome.None);
    }

    [Fact]
    public void A_change_within_the_threshold_passes()
    {
        var outcome = DeltaCheck.Evaluate(4.4m, 4.0m, PreviousAt, 20m);

        outcome.Percent.ShouldBe(10.0m);
        outcome.Exceeded.ShouldBeFalse();
        outcome.PreviousValue.ShouldBe(4.0m);
        outcome.PreviousAt.ShouldBe(PreviousAt);
    }

    [Fact]
    public void A_change_beyond_the_threshold_fails()
    {
        // Haemoglobin 12.0 -> 8.0 g/dL: a 33 % drop, likely wrong patient or a bleed.
        var outcome = DeltaCheck.Evaluate(8.0m, 12.0m, PreviousAt, 20m);

        outcome.Percent.ShouldBe(33.3m);
        outcome.Exceeded.ShouldBeTrue();
    }

    [Fact]
    public void Exactly_the_threshold_still_passes()
    {
        DeltaCheck.Evaluate(1.2m, 1.0m, PreviousAt, 20m).Exceeded.ShouldBeFalse();
        DeltaCheck.Evaluate(1.21m, 1.0m, PreviousAt, 20m).Exceeded.ShouldBeTrue();
    }

    [Fact]
    public void A_change_away_from_zero_has_no_percentage_and_is_flagged()
    {
        var outcome = DeltaCheck.Evaluate(3.0m, 0m, PreviousAt, 50m);

        outcome.Percent.ShouldBeNull();
        outcome.Exceeded.ShouldBeTrue();
        DeltaCheck.Evaluate(0m, 0m, PreviousAt, 50m).Exceeded.ShouldBeFalse();
    }

    [Fact]
    public void Negative_previous_values_use_the_magnitude()
    {
        // Base excess -4 -> -5 is a 25 % change.
        DeltaCheck.Evaluate(-5m, -4m, PreviousAt, 50m).Percent.ShouldBe(25.0m);
    }

    [Fact]
    public void A_threshold_of_zero_switches_the_check_off()
    {
        var outcome = DeltaCheck.Evaluate(20m, 1m, PreviousAt, 0m);

        outcome.Percent.ShouldBe(1900.0m);
        outcome.Exceeded.ShouldBeFalse();
    }
}
