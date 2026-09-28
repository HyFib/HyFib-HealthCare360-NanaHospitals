namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>A catalogue entry as seen by the ranker.</summary>
public sealed record CodeCandidate(string Code, string Title, string? Keywords, bool IsBillable);

/// <summary>
/// Ranks local ICD/procedure search results (MRD-01). The database narrows candidates with ILIKE (code prefix or
/// every query word); this pure function orders them the way coders expect: exact code, then code prefix
/// (category before its subdivisions), then titles that start with the query, whole-word title matches, keyword
/// (shorthand) matches and finally loose matches. Shorter titles win ties because they are usually the intended term.
/// </summary>
public static class IcdSearchRanker
{
    private static readonly char[] Separators = [' ', ',', '(', ')', '[', ']', '-', '/', ';', ':', '.'];

    /// <summary>True for text that looks like a code ("E11", "e11.9", "E119", "0DTJ4ZZ").</summary>
    public static bool LooksLikeCode(string query)
    {
        var q = query.Trim();
        return q.Length >= 2 && !q.Contains(' ', StringComparison.Ordinal) && q.Any(char.IsAsciiDigit) && char.IsAsciiLetterOrDigit(q[0]);
    }

    /// <summary>Upper-cases and restores the ICD-10 dot: "e119" → "E11.9". Procedure codes are returned upper-cased.</summary>
    public static string NormalizeCode(string query)
    {
        var q = query.Trim().ToUpperInvariant();
        var isIcd10 = q.Length > 3 && char.IsAsciiLetterUpper(q[0]) && char.IsAsciiDigit(q[1]) && char.IsAsciiDigit(q[2]) && q[3] != '.'
                      && q.Length <= 5 && q[3..].All(char.IsAsciiDigit);
        return isIcd10 ? $"{q[..3]}.{q[3..]}" : q;
    }

    public static IReadOnlyList<string> Words(string query) =>
        query.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToList();

    public static int Score(CodeCandidate candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        var q = query.Trim();
        var code = candidate.Code.ToUpperInvariant();
        var normalizedCode = NormalizeCode(q);
        var title = candidate.Title.ToLowerInvariant();
        var lowerQuery = q.ToLowerInvariant();
        // Among equally relevant titles prefer billable (specific) codes, then the shorter, more general wording.
        var tieBreak = -Math.Min(title.Length, 200) / 10 + (candidate.IsBillable ? 5 : 0);

        if (code == normalizedCode || code.Replace(".", string.Empty, StringComparison.Ordinal) == normalizedCode.Replace(".", string.Empty, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (code.StartsWith(normalizedCode, StringComparison.Ordinal))
        {
            return 900 - Math.Min(code.Length - normalizedCode.Length, 9) * 10;
        }

        if (title == lowerQuery)
        {
            return 800;
        }

        if (title.StartsWith(lowerQuery, StringComparison.Ordinal))
        {
            return 700 + tieBreak;
        }

        var words = Words(q);
        if (words.Count == 0)
        {
            return 0;
        }

        var titleWords = Words(candidate.Title);
        if (words.All(w => titleWords.Any(t => t.StartsWith(w, StringComparison.Ordinal))))
        {
            // Bonus when the words appear in the order typed ("acute appendicitis" before "appendicitis, acute").
            var inOrder = title.Contains(lowerQuery, StringComparison.Ordinal) ? 50 : 0;
            return 500 + inOrder + tieBreak;
        }

        var keywords = candidate.Keywords?.ToLowerInvariant() ?? string.Empty;
        var keywordWords = Words(keywords);
        if (keywords.Length > 0 && words.All(w => keywordWords.Any(k => k.StartsWith(w, StringComparison.Ordinal)) || titleWords.Any(t => t.StartsWith(w, StringComparison.Ordinal))))
        {
            return 400 + tieBreak;
        }

        var haystack = $"{title} {keywords}";
        return words.All(w => haystack.Contains(w, StringComparison.Ordinal)) ? 100 + tieBreak : 0;
    }

    /// <summary>Scores, drops non-matches and returns the best <paramref name="limit"/> candidates.</summary>
    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> items, Func<T, CodeCandidate> candidate, string query, int limit) =>
        items.Select(item => (Item: item, Candidate: candidate(item)))
            .Select(x => (x.Item, x.Candidate, Score: Score(x.Candidate, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Code, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => x.Item)
            .ToList();
}
