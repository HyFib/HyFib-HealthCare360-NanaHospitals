namespace HyFib.HealthCare360.Modules.Engagement.Domain;

public sealed record SentimentResult(Sentiment Sentiment, int Score);

/// <summary>
/// Simple, explainable rule-based sentiment for feedback comments (FB-04): an English + common Hinglish/Tamil
/// word list, negation ("not good", "nahi accha") flipping the next sentiment word, plus the ratings and NPS the
/// patient gave. Deliberately not an AI service: it runs offline, costs nothing and never sends comments anywhere.
/// </summary>
public static class SentimentAnalyzer
{
    private const int WordWeight = 2;
    private const int NegationReach = 3;

    private static readonly HashSet<string> Positive = new(StringComparer.Ordinal)
    {
        "good", "great", "excellent", "clean", "caring", "kind", "helpful", "polite", "quick", "fast", "friendly", "satisfied",
        "thank", "thanks", "best", "nice", "professional", "courteous", "prompt", "supportive", "recommend", "happy", "wonderful",
        "attentive", "smooth", "efficient", "accha", "acha", "achha", "badhiya", "shukriya", "dhanyavad", "nandri", "super",
    };

    private static readonly HashSet<string> Negative = new(StringComparer.Ordinal)
    {
        "bad", "poor", "worst", "dirty", "rude", "slow", "delay", "delayed", "waiting", "wait", "expensive", "overcharged",
        "careless", "unhygienic", "unprofessional", "crowded", "noisy", "disappointed", "horrible", "terrible", "smell", "smelly",
        "unclean", "ignored", "negligent", "bekar", "bekaar", "ganda", "kharab", "galat", "mosam",
    };

    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "dont", "didnt", "wasnt", "isnt", "werent", "nahi", "nahin", "illa",
    };

    public static SentimentResult Analyze(string? comment, FeedbackRatings ratings, int? nps)
    {
        var score = ScoreText(comment);

        if (ratings.Average is { } average)
        {
            score += (int)Math.Round((average - 3) * 2, MidpointRounding.AwayFromZero);
        }

        score += nps switch
        {
            >= 9 => 2,
            <= 6 => -2,
            _ => 0,
        };

        var sentiment = score >= 2 ? Sentiment.Positive : score <= -2 ? Sentiment.Negative : Sentiment.Neutral;
        return new SentimentResult(sentiment, score);
    }

    /// <summary>Lexicon score of the text alone (+2 per positive word, −2 per negative word, flipped after a negation).</summary>
    public static int ScoreText(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return 0;
        }

        var words = new string(comment.ToLowerInvariant().Select(c => char.IsLetter(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var score = 0;
        var negateFor = 0;
        foreach (var word in words)
        {
            if (Negations.Contains(word))
            {
                negateFor = NegationReach;
                continue;
            }

            var value = Positive.Contains(word) ? WordWeight : Negative.Contains(word) ? -WordWeight : 0;
            if (value != 0 && negateFor > 0)
            {
                value = -value;
                negateFor = 0;
            }
            else if (negateFor > 0)
            {
                negateFor--;
            }

            score += value;
        }

        return score;
    }
}
