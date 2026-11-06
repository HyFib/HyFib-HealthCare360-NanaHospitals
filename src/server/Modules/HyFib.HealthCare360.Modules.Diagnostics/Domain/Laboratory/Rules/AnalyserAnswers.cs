namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// Reads an answer written another way as the test's own answer (pure; unit-tested). Urine strip readers and
/// immunoassay analysers send "Neg", "+-", "++" or "Pos" where HC360's urine tests offer Nil, Trace, 2+ or Positive. A
/// word is read as one of the test's answers only when it names the same grade and exactly one of the test's answers
/// does: "Neg" is Nil for protein and Negative for nitrite, "+-" is Trace, "++" is 2+. A word that names another grade
/// than any the test offers ("1+" for a test answered Negative or Positive) or a number ("15 mg/dL") is not read as
/// anything: turning a strip's concentration into a grade needs the strip maker's chart, so such a result still waits
/// for a person among the unmatched results. The analyser's own word is kept on the result (LabResult.InstrumentValue).
/// </summary>
internal static class AnalyserAnswers
{
    /// <summary>Ways of writing one grade, each as <see cref="Key"/> reads it (no spaces, upper case).</summary>
    private static readonly string[][] Grades =
    [
        ["NIL", "NEG", "NEGATIVE", "ABSENT", "NOTDETECTED"],
        ["TRACE", "TR", "+-", "+/-", "±", "-+"],
        ["1+", "+", "+1"],
        ["2+", "++", "+2"],
        ["3+", "+++", "+3"],
        ["4+", "++++", "+4"],
        ["POSITIVE", "POS", "PRESENT", "DETECTED"],
        ["NORMAL", "NORM"],
        ["INCREASED", "INCR"],
    ];

    /// <summary>The test's answer the word names, or null when it names none of them or more than one.</summary>
    public static string? Match(IReadOnlyList<string> options, string word)
    {
        var key = Key(word);
        var grade = Array.Find(Grades, g => g.Contains(key, StringComparer.Ordinal));
        if (grade is null)
        {
            return null;
        }

        var answers = options.Where(o => grade.Contains(Key(o), StringComparer.Ordinal)).ToList();
        return answers.Count == 1 ? answers[0] : null;
    }

    private static string Key(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}
