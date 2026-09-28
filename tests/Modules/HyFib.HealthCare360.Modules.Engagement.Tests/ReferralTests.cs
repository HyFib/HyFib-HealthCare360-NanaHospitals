using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class ReferralTests
{
    private static readonly DateTimeOffset March = new(2026, 3, 10, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset April = new(2026, 4, 12, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Dr. R. Kumar", "r kumar")]
    [InlineData("  DR   ravi-kumar ", "ravi kumar")]
    [InlineData("Prof. Dr. S.Anand, MD", "s anand md")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Names_are_normalised_without_honorifics_or_punctuation(string? input, string expected) =>
        ReferralNameMatcher.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("Dr. R. Kumar, Anna Nagar", true)]
    [InlineData("r kumar", true)]
    [InlineData("Dr Ravi Kumar", true)]
    [InlineData("Dr. S. Kumar", false)]
    [InlineData("Kumar", false)]
    [InlineData("", false)]
    public void Referrer_text_matches_a_multi_word_name_as_consecutive_words(string text, bool expected) =>
        ReferralNameMatcher.Matches(text, ["R. Kumar", "Ravi Kumar"]).ShouldBe(expected);

    [Fact]
    public void Single_word_names_must_match_the_whole_text()
    {
        ReferralNameMatcher.Matches("Dr. Anand", ["Anand"]).ShouldBeTrue();
        ReferralNameMatcher.Matches("Dr. Anand Clinic Tambaram", ["Anand"]).ShouldBeFalse();
    }

    [Fact]
    public void The_key_token_is_the_longest_word() => ReferralNameMatcher.KeyToken("Dr. R. Kumaraswamy").ShouldBe("kumaraswamy");

    [Fact]
    public void Each_referral_counts_once_for_the_most_specific_doctor()
    {
        var kumar = new ReferrerNames(Guid.Parse("00000000-0000-0000-0000-000000000001"), ["Kumar"]);
        var raviKumar = new ReferrerNames(Guid.Parse("00000000-0000-0000-0000-000000000002"), ["Ravi Kumar", "R. Kumar"]);
        ReferrerText[] texts =
        [
            new("Dr Ravi Kumar", 5, March),
            new("Dr. R. Kumar, Anna Nagar", 2, April),
            new("Kumar", 3, March),
            new("Dr Meena", 4, April),
        ];

        var (byDoctor, unmatched) = ReferralAttribution.Attribute([kumar, raviKumar], texts);

        byDoctor[raviKumar.DoctorId].ShouldBe(new ReferralTally(7, April));
        byDoctor[kumar.DoctorId].ShouldBe(new ReferralTally(3, March));
        unmatched.ShouldHaveSingleItem().Text.ShouldBe("Dr Meena");
    }

    [Fact]
    public void Ties_between_equally_specific_names_resolve_to_one_doctor_deterministically()
    {
        var first = new ReferrerNames(Guid.Parse("00000000-0000-0000-0000-00000000000a"), ["Priya Menon"]);
        var second = new ReferrerNames(Guid.Parse("00000000-0000-0000-0000-00000000000b"), ["Priya Menon"]);

        ReferralAttribution.DoctorFor("Dr Priya Menon", [second, first]).ShouldBe(first.DoctorId);
        ReferralAttribution.DoctorFor("Dr Priya Menon", [first, second]).ShouldBe(first.DoctorId);
    }

    [Fact]
    public void Unmatched_texts_are_listed_by_volume()
    {
        var (_, unmatched) = ReferralAttribution.Attribute([], [new("Dr A", 1, March), new("Dr B", 9, March)]);

        unmatched.Select(u => u.Text).ShouldBe(["Dr B", "Dr A"]);
    }
}
