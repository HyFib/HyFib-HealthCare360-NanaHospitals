using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class SentimentAnalyzerTests
{
    [Theory]
    [InlineData("Doctor was very kind and the staff were helpful", 4)]
    [InlineData("Rude billing staff and a long wait", -4)]
    [InlineData("The room was not clean", -2)]
    [InlineData("Not bad at all", 2)]
    [InlineData("Nurses were accha, khana bekar tha", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Comments_are_scored_from_the_word_list_with_negation(string? comment, int expected) =>
        SentimentAnalyzer.ScoreText(comment).ShouldBe(expected);

    [Fact]
    public void Negation_reaches_only_a_few_words()
    {
        // A negation reaches the next three words; "good" comes after five neutral words, so it stays positive.
        SentimentAnalyzer.ScoreText("not that I would say it good").ShouldBe(2);
    }

    [Fact]
    public void Ratings_and_recommendation_score_count_alongside_the_text()
    {
        var ratings = new FeedbackRatings(5, 5, null, null, null, null, 5);

        SentimentAnalyzer.Analyze(null, ratings, 10).ShouldBe(new SentimentResult(Sentiment.Positive, 6));
        SentimentAnalyzer.Analyze("Terrible wait", new FeedbackRatings(1, null, null, null, null, null, 1), 2).Sentiment.ShouldBe(Sentiment.Negative);
        SentimentAnalyzer.Analyze(null, new FeedbackRatings(3, null, null, null, null, null, 3), 8).ShouldBe(new SentimentResult(Sentiment.Neutral, 0));
    }

    [Fact]
    public void Kind_words_do_not_hide_very_low_ratings()
    {
        var result = SentimentAnalyzer.Analyze("Thanks", new FeedbackRatings(1, 1, null, null, null, null, 1), 0);

        result.Sentiment.ShouldBe(Sentiment.Negative);
    }
}
