using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class FeedbackScoringTests
{
    private static FeedbackRatings Ratings(int? doctor = null, int? nursing = null, int? food = null, int? overall = null) =>
        new(doctor, nursing, null, null, null, food, overall);

    [Fact]
    public void Any_aspect_rated_two_or_less_is_a_low_score()
    {
        LowScoreRule.IsLow(Ratings(doctor: 5, food: 2), null).ShouldBeTrue();
        LowScoreRule.LowAspects(Ratings(doctor: 5, food: 2, nursing: 1)).ShouldBe([FeedbackAspect.Nursing, FeedbackAspect.Food]);
        LowScoreRule.IsLow(Ratings(doctor: 3, food: 3), null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(null, false)]
    public void A_detractor_recommendation_score_is_a_low_score(int? nps, bool low) =>
        LowScoreRule.IsLow(Ratings(overall: 4), nps).ShouldBe(low);

    [Fact]
    public void The_worst_answers_get_the_24_hour_priority()
    {
        LowScoreRule.PriorityFor(Ratings(doctor: 1), null).ShouldBe(ComplaintPriority.High);
        LowScoreRule.PriorityFor(Ratings(overall: 4), 3).ShouldBe(ComplaintPriority.High);
        LowScoreRule.PriorityFor(Ratings(doctor: 2), 5).ShouldBe(ComplaintPriority.Normal);
    }

    [Fact]
    public void Nps_is_percent_promoters_minus_percent_detractors()
    {
        var result = NpsCalculator.Compute([10, 9, 9, 8, 7, 6, 3]);

        result.ShouldBe(new NpsResult(7, 3, 2, 2, 14.3m));
    }

    [Fact]
    public void Nps_ignores_out_of_range_answers_and_is_empty_without_answers()
    {
        NpsCalculator.Compute([11, -1]).ShouldBe(new NpsResult(0, 0, 0, 0, null));
        NpsCalculator.Compute([0, 0]).Score.ShouldBe(-100m);
        NpsCalculator.Compute([10]).Score.ShouldBe(100m);
    }

    [Fact]
    public void Ratings_average_only_the_aspects_answered()
    {
        Ratings(doctor: 5, food: 2).Average.ShouldBe(3.5m);
        FeedbackRatings.None.Average.ShouldBeNull();
        Ratings(doctor: 4, nursing: 4, overall: 5).Average.ShouldBe(4.33m);
    }

    [Fact]
    public void Forms_list_only_known_aspects_once()
    {
        var form = new FeedbackForm { Aspects = ["Doctor", "Food", "Doctor", "Parking"] };

        form.AspectList.ShouldBe([FeedbackAspect.Doctor, FeedbackAspect.Food]);
    }

    [Fact]
    public void Answers_must_fit_the_form()
    {
        var form = new FeedbackForm { Title = "OPD", Aspects = ["Doctor", "Overall"], IncludesNps = false, AllowsComment = false };

        FeedbackService.ValidateAgainstForm(form, Submission(Ratings(doctor: 4))).IsSuccess.ShouldBeTrue();
        FeedbackService.ValidateAgainstForm(form, Submission(Ratings(food: 4))).Error!.Fields!.ShouldContainKey("ratings");
        FeedbackService.ValidateAgainstForm(form, Submission(Ratings(doctor: 6))).Error!.Fields!.ShouldContainKey("ratings");
        FeedbackService.ValidateAgainstForm(form, Submission(Ratings(doctor: 4), nps: 9)).Error!.Fields!.ShouldContainKey("nps");
        FeedbackService.ValidateAgainstForm(form, Submission(Ratings(doctor: 4), comment: "Nice")).Error!.Fields!.ShouldContainKey("comment");
        FeedbackService.ValidateAgainstForm(form, Submission(FeedbackRatings.None)).Error!.Fields!.ShouldContainKey("ratings");
    }

    [Fact]
    public void A_call_back_needs_a_valid_phone_number()
    {
        var form = new FeedbackForm { Title = "OPD", Aspects = ["Overall"] };

        FeedbackService.ValidateAgainstForm(form, Submission(Ratings(overall: 1), wantsCallback: true)).Error!.Fields!.ShouldContainKey("contactPhone");
        FeedbackService.ValidateAgainstForm(form, Submission(Ratings(overall: 1), wantsCallback: true, phone: "98765 43210")).IsSuccess.ShouldBeTrue();
    }

    private static FeedbackSubmission Submission(FeedbackRatings ratings, int? nps = null, string? comment = null, bool wantsCallback = false, string? phone = null) =>
        new(ratings, nps, comment, phone, wantsCallback);
}
