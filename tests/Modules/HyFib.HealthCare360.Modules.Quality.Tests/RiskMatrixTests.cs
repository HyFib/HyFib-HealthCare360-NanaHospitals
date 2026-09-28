using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class RiskMatrixTests
{
    [Theory]
    [InlineData(1, 1, 1, RiskRating.Low)]
    [InlineData(2, 2, 4, RiskRating.Low)]
    [InlineData(1, 5, 5, RiskRating.Medium)]
    [InlineData(3, 3, 9, RiskRating.Medium)]
    [InlineData(2, 5, 10, RiskRating.High)]
    [InlineData(3, 5, 15, RiskRating.High)]
    [InlineData(4, 4, 16, RiskRating.High)]
    [InlineData(4, 5, 20, RiskRating.Extreme)]
    [InlineData(5, 5, 25, RiskRating.Extreme)]
    public void Scores_likelihood_times_impact_and_rates_the_band(int likelihood, int impact, int score, RiskRating rating)
    {
        RiskMatrix.Score(likelihood, impact).ShouldBe(score);
        RiskMatrix.Rate(likelihood, impact).ShouldBe(rating);
    }

    [Fact]
    public void The_five_by_five_grid_has_the_expected_band_sizes()
    {
        var ratings = Enumerable.Range(1, 5).SelectMany(l => Enumerable.Range(1, 5).Select(i => RiskMatrix.Rate(l, i))).ToList();

        ratings.Count(r => r == RiskRating.Low).ShouldBe(8);
        ratings.Count(r => r == RiskRating.Medium).ShouldBe(7);
        ratings.Count(r => r == RiskRating.High).ShouldBe(7);
        ratings.Count(r => r == RiskRating.Extreme).ShouldBe(3);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 6)]
    public void Levels_outside_one_to_five_are_rejected(int likelihood, int impact) =>
        Should.Throw<ArgumentOutOfRangeException>(() => RiskMatrix.Score(likelihood, impact));

    [Fact]
    public void Assessing_a_risk_stores_inherent_and_residual_scores()
    {
        var risk = new Risk();

        risk.Assess(4, 5, 2, 5).IsSuccess.ShouldBeTrue();

        risk.Score.ShouldBe(20);
        risk.Rating.ShouldBe(RiskRating.Extreme);
        risk.ResidualScore.ShouldBe(10);
        risk.ResidualRating.ShouldBe(RiskRating.High);
    }

    [Fact]
    public void Residual_risk_cannot_exceed_the_inherent_risk()
    {
        new Risk().Assess(2, 2, 3, 3).Error!.Code.ShouldBe("risk.residual_above_inherent");
    }

    [Fact]
    public void Residual_scores_come_in_pairs_and_within_range()
    {
        new Risk().Assess(3, 3, 2, null).IsFailure.ShouldBeTrue();
        new Risk().Assess(3, 3, 0, 1).IsFailure.ShouldBeTrue();
        new Risk().Assess(6, 1, null, null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Reviews_must_set_a_future_review_date()
    {
        var today = new DateOnly(2026, 9, 25);
        var risk = new Risk { ReviewDate = today.AddDays(-3) };

        risk.IsReviewOverdue(today).ShouldBeTrue();
        risk.RecordReview(today, "Reviewed", today, DateTimeOffset.UnixEpoch).IsFailure.ShouldBeTrue();
        risk.RecordReview(today.AddMonths(3), " Controls effective ", today, DateTimeOffset.UnixEpoch).IsSuccess.ShouldBeTrue();

        risk.IsReviewOverdue(today).ShouldBeFalse();
        risk.LastReviewNote.ShouldBe("Controls effective");
    }

    [Fact]
    public void Closed_risks_are_never_overdue()
    {
        new Risk { ReviewDate = new DateOnly(2020, 1, 1), Status = RiskStatus.Closed }.IsReviewOverdue(new DateOnly(2026, 9, 25)).ShouldBeFalse();
    }
}
