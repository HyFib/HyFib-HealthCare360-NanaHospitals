using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// A strip reader's word is read as the test's answer when it names the same grade and only one of the test's answers
/// does; a grade the test does not offer, or a concentration, is not read as anything.
/// </summary>
public sealed class AnalyserAnswersTests
{
    private static readonly string[] StripGrades = ["Nil", "Trace", "1+", "2+", "3+", "4+"];
    private static readonly string[] NegativePositive = ["Negative", "Positive"];
    private static readonly string[] Esterase = ["Negative", "Trace", "1+", "2+", "3+"];
    private static readonly string[] Urobilinogen = ["Normal", "Increased"];

    [Theory]
    [InlineData("Neg", "Nil")]
    [InlineData("NEGATIVE", "Nil")]
    [InlineData("not detected", "Nil")]
    [InlineData("+-", "Trace")]
    [InlineData("±", "Trace")]
    [InlineData("TR", "Trace")]
    [InlineData("+", "1+")]
    [InlineData("++", "2+")]
    [InlineData("+3", "3+")]
    [InlineData("++++", "4+")]
    public void A_strip_grade_written_another_way_is_the_tests_grade(string word, string answer) =>
        AnalyserAnswers.Match(StripGrades, word).ShouldBe(answer);

    [Theory]
    [InlineData("NEG", "Negative")]
    [InlineData("Nil", "Negative")]
    [InlineData("POS", "Positive")]
    [InlineData("present", "Positive")]
    public void Negative_and_positive_are_read_whichever_word_the_test_uses(string word, string answer) =>
        AnalyserAnswers.Match(NegativePositive, word).ShouldBe(answer);

    [Fact]
    public void The_same_word_finds_the_tests_own_spelling_of_the_grade()
    {
        AnalyserAnswers.Match(Esterase, "neg").ShouldBe("Negative");
        AnalyserAnswers.Match(Esterase, "+-").ShouldBe("Trace");
        AnalyserAnswers.Match(Urobilinogen, "NORM").ShouldBe("Normal");
    }

    [Theory]
    [InlineData("1+")]
    [InlineData("++")]
    [InlineData("Trace")]
    public void A_grade_the_test_does_not_offer_is_not_turned_into_another(string word) =>
        AnalyserAnswers.Match(NegativePositive, word).ShouldBeNull();

    [Theory]
    [InlineData("15 mg/dL")]
    [InlineData("3.2 umol/L")]
    [InlineData("-")]
    [InlineData("Reactive")]
    [InlineData("")]
    public void A_concentration_or_a_word_of_no_grade_is_not_read(string word) =>
        AnalyserAnswers.Match(StripGrades, word).ShouldBeNull();

    [Fact]
    public void A_word_two_of_the_tests_answers_share_is_not_guessed()
    {
        AnalyserAnswers.Match(["Nil", "Absent", "Present"], "NEG").ShouldBeNull();
        AnalyserAnswers.Match(["Nil", "Absent", "Present"], "POS").ShouldBe("Present");
    }
}
