using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Quality;

/// <summary>Westgard multirules over z-scores (oldest first, new run last).</summary>
public sealed class WestgardEvaluatorTests
{
    [Fact]
    public void A_run_within_two_SD_is_accepted()
    {
        var evaluation = WestgardEvaluator.Evaluate([0.4m, -0.8m, 1.1m], -0.6m);

        evaluation.Outcome.ShouldBe(QcOutcome.Accepted);
        evaluation.Violations.ShouldBeEmpty();
        evaluation.IsRejected.ShouldBeFalse();
    }

    [Fact]
    public void The_first_run_of_a_new_lot_is_judged_on_its_own() =>
        WestgardEvaluator.Evaluate([], 1.4m).Outcome.ShouldBe(QcOutcome.Accepted);

    [Theory]
    [InlineData(2.4)]
    [InlineData(-2.9)]
    public void One_run_beyond_two_SD_is_a_warning_not_a_rejection(double z)
    {
        var evaluation = WestgardEvaluator.Evaluate([0.3m, -0.2m], (decimal)z);

        evaluation.Outcome.ShouldBe(QcOutcome.AcceptedWithWarning);
        evaluation.Violations.ShouldBe([WestgardEvaluator.Warning12s]);
    }

    [Theory]
    [InlineData(3.2)]
    [InlineData(-3.01)]
    public void One_run_beyond_three_SD_is_rejected_as_random_error(double z)
    {
        var evaluation = WestgardEvaluator.Evaluate([0.3m, -0.2m], (decimal)z);

        evaluation.Outcome.ShouldBe(QcOutcome.Rejected);
        evaluation.Violations.ShouldBe([WestgardEvaluator.Reject13s]);
    }

    [Fact]
    public void Limits_are_strict_so_a_run_exactly_on_a_limit_does_not_cross_it()
    {
        WestgardEvaluator.Evaluate([0.1m], 2.0m).Outcome.ShouldBe(QcOutcome.Accepted);
        WestgardEvaluator.Evaluate([0.1m], -2.0m).Outcome.ShouldBe(QcOutcome.Accepted);
        WestgardEvaluator.Evaluate([0.1m], 3.0m).Violations.ShouldBe([WestgardEvaluator.Warning12s]);
        WestgardEvaluator.Evaluate([1.0m, 1.0m, 1.0m], 1.0m).Outcome.ShouldBe(QcOutcome.Accepted);
    }

    [Theory]
    [InlineData(2.3, 2.1)]
    [InlineData(-2.6, -2.2)]
    public void Two_consecutive_runs_beyond_two_SD_on_the_same_side_are_rejected(double previous, double current)
    {
        var evaluation = WestgardEvaluator.Evaluate([0.2m, (decimal)previous], (decimal)current);

        evaluation.Outcome.ShouldBe(QcOutcome.Rejected);
        evaluation.Violations.ShouldBe([WestgardEvaluator.Reject22s]);
    }

    [Theory]
    [InlineData(2.5, -2.2)]
    [InlineData(-2.1, 2.4)]
    public void A_range_of_more_than_four_SD_between_consecutive_runs_is_rejected(double previous, double current)
    {
        var evaluation = WestgardEvaluator.Evaluate([0.2m, (decimal)previous], (decimal)current);

        evaluation.Outcome.ShouldBe(QcOutcome.Rejected);
        evaluation.Violations.ShouldBe([WestgardEvaluator.RejectR4s]);
    }

    [Fact]
    public void A_gradual_shift_beyond_one_SD_over_four_runs_is_rejected_although_no_run_crosses_two_SD()
    {
        WestgardEvaluator.Evaluate([0.2m, 1.2m, 1.5m, 1.1m], 1.3m).Violations.ShouldBe([WestgardEvaluator.Reject41s]);
        WestgardEvaluator.Evaluate([-1.4m, -1.1m, -1.9m], -1.2m).Violations.ShouldBe([WestgardEvaluator.Reject41s]);
    }

    [Fact]
    public void Three_runs_beyond_one_SD_are_not_yet_a_shift() =>
        WestgardEvaluator.Evaluate([0.2m, 1.5m, 1.1m], 1.3m).Outcome.ShouldBe(QcOutcome.Accepted);

    [Fact]
    public void Ten_consecutive_runs_on_one_side_of_the_mean_are_rejected_as_a_bias()
    {
        decimal[] history = [0.3m, 0.5m, 0.2m, 0.8m, 0.4m, 0.6m, 0.1m, 0.7m, 0.2m];

        var evaluation = WestgardEvaluator.Evaluate(history, 0.4m);

        evaluation.Outcome.ShouldBe(QcOutcome.Rejected);
        evaluation.Violations.ShouldBe([WestgardEvaluator.Reject10x]);
    }

    [Fact]
    public void Nine_runs_on_one_side_are_not_yet_a_bias() =>
        WestgardEvaluator.Evaluate([0.3m, 0.5m, 0.2m, 0.8m, 0.4m, 0.6m, 0.1m, 0.7m], 0.4m).Outcome.ShouldBe(QcOutcome.Accepted);

    [Fact]
    public void A_long_history_is_trimmed_to_the_runs_the_rules_look_at()
    {
        // The oldest run (below the mean) falls outside the ten-run window, so the ten most recent runs still count.
        decimal[] history = [-0.5m, 0.3m, 0.5m, 0.2m, 0.8m, 0.4m, 0.6m, 0.1m, 0.7m, 0.2m];

        WestgardEvaluator.Evaluate(history, 0.4m).Violations.ShouldBe([WestgardEvaluator.Reject10x]);
        WestgardEvaluator.Evaluate(history[..^1], 0.4m).Outcome.ShouldBe(QcOutcome.Accepted);
    }

    [Fact]
    public void Every_violated_rejection_rule_is_reported_and_the_warning_is_implied()
    {
        var evaluation = WestgardEvaluator.Evaluate([1.4m, 1.2m, 2.5m], 3.4m);

        evaluation.Outcome.ShouldBe(QcOutcome.Rejected);
        evaluation.Violations.ShouldBe([WestgardEvaluator.Reject13s, WestgardEvaluator.Reject22s, WestgardEvaluator.Reject41s]);
        evaluation.Violations.ShouldNotContain(WestgardEvaluator.Warning12s);
    }

    [Fact]
    public void Control_values_are_converted_to_z_scores_against_the_lot_targets()
    {
        var lot = new QcLot { TargetMean = 100m, TargetSd = 4m };

        lot.ZScore(108m).ShouldBe(2.0m);
        lot.ZScore(89m).ShouldBe(-2.75m);
        new QcLot { TargetMean = 5.0m, TargetSd = 0.3m }.ZScore(5.1m).ShouldBe(0.333m);
        new QcLot { TargetMean = 5.0m, TargetSd = 0m }.ZScore(9m).ShouldBe(0m);
    }

    [Fact]
    public void A_lot_is_usable_up_to_and_including_its_expiry_date()
    {
        var lot = new QcLot { ExpiryDate = new DateOnly(2026, 9, 25) };

        lot.IsExpiredOn(new DateOnly(2026, 9, 25)).ShouldBeFalse();
        lot.IsExpiredOn(new DateOnly(2026, 9, 26)).ShouldBeTrue();
    }

    [Fact]
    public void A_rejected_run_needs_a_corrective_action_before_it_is_reviewed()
    {
        var run = new QcRun { Outcome = QcOutcome.Rejected, Violations = [WestgardEvaluator.Reject13s] };

        run.Review("Checked", " ", UserId, Now).Error!.Fields!.Keys.ShouldContain("correctiveAction");
        run.ReviewedBy.ShouldBeNull();

        run.Review(" Checked ", " Recalibrated and repeated; control within 1 SD ", UserId, Now).IsSuccess.ShouldBeTrue();
        run.CorrectiveAction.ShouldBe("Recalibrated and repeated; control within 1 SD");
        run.ReviewNote.ShouldBe("Checked");
        run.ReviewedBy.ShouldBe(UserId);
        run.ReviewedAt.ShouldBe(Now);
    }

    [Fact]
    public void An_accepted_run_is_reviewed_without_a_corrective_action() =>
        new QcRun { Outcome = QcOutcome.AcceptedWithWarning }.Review("Trend noted", null, UserId, Now).IsSuccess.ShouldBeTrue();
}
