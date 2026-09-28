namespace HyFib.HealthCare360.Modules.Engagement.Domain;

/// <summary>When a feedback needs attention (FB-03): any aspect rated 2 or less, or a detractor NPS (6 or less).</summary>
public static class LowScoreRule
{
    public const int LowRating = 2;
    public const int LowNps = 6;

    public static bool IsLow(FeedbackRatings ratings, int? nps) =>
        ratings.Given.Any(g => g.Rating <= LowRating) || nps is <= LowNps;

    public static IReadOnlyList<FeedbackAspect> LowAspects(FeedbackRatings ratings) =>
        ratings.Given.Where(g => g.Rating <= LowRating).Select(g => g.Aspect).ToList();

    /// <summary>The worst answers (a 1-star rating or NPS 3 or less) are handled within 24 hours instead of 72.</summary>
    public static ComplaintPriority PriorityFor(FeedbackRatings ratings, int? nps) =>
        ratings.Given.Any(g => g.Rating <= 1) || nps is <= 3 ? ComplaintPriority.High : ComplaintPriority.Normal;
}

public sealed record NpsResult(int Responses, int Promoters, int Passives, int Detractors, decimal? Score);

/// <summary>
/// Net Promoter Score: promoters answer 9–10, passives 7–8, detractors 0–6; NPS = % promoters − % detractors
/// (−100 … +100), one decimal.
/// </summary>
public static class NpsCalculator
{
    public static NpsResult Compute(IEnumerable<int> answers)
    {
        var scores = answers.Where(a => a is >= 0 and <= 10).ToList();
        if (scores.Count == 0)
        {
            return new NpsResult(0, 0, 0, 0, null);
        }

        var promoters = scores.Count(s => s >= 9);
        var detractors = scores.Count(s => s <= 6);
        var passives = scores.Count - promoters - detractors;
        var score = Math.Round((promoters - detractors) * 100m / scores.Count, 1, MidpointRounding.AwayFromZero);
        return new NpsResult(scores.Count, promoters, passives, detractors, score);
    }
}
