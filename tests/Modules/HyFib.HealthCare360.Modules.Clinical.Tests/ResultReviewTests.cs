using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>A released report on the doctor's results to review (EMR-01): one review per report, re-opened by later releases.</summary>
public sealed class ResultReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero);

    private static ResultReview Review(bool critical) => new()
    {
        PatientId = Guid.CreateVersion7(),
        ReportId = Guid.CreateVersion7(),
        Kind = DiagnosticKind.Laboratory,
        Title = "Kidney function test",
        HasCriticalFindings = critical,
        ReleasedAt = Now,
    };

    [Fact]
    public void A_critical_result_not_yet_acknowledged_stays_critical_whatever_is_released_after_it()
    {
        var review = Review(critical: true);

        review.Reopen("Complete blood count", hasCriticalFindings: false, Now.AddHours(2));

        review.HasCriticalFindings.ShouldBeTrue("The critical potassium has not been seen yet.");
        review.Title.ShouldBe("Complete blood count");
        review.ReleasedAt.ShouldBe(Now.AddHours(2));
        review.IsReviewed.ShouldBeFalse();
    }

    [Fact]
    public void A_release_after_the_acknowledgement_needs_a_fresh_review_with_its_own_flag()
    {
        var review = Review(critical: true);
        review.Acknowledge(Guid.CreateVersion7(), "Potassium corrected", Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();

        review.Reopen("Complete blood count", hasCriticalFindings: false, Now.AddHours(2));

        review.IsReviewed.ShouldBeFalse();
        review.ReviewNote.ShouldBeNull();
        review.HasCriticalFindings.ShouldBeFalse("The critical result was acknowledged; what came after is not critical.");

        review.Reopen("Kidney function test (amended)", hasCriticalFindings: true, Now.AddHours(3));
        review.HasCriticalFindings.ShouldBeTrue();
    }
}
