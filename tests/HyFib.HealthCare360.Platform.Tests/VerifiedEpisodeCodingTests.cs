using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>Verified medical records coding as the text of an insurance claim's codes (MB-03).</summary>
public sealed class VerifiedEpisodeCodingTests
{
    private static readonly CodedEntry Cellulitis = new("L03.1", "Cellulitis of other parts of limb", IsPrimary: true);
    private static readonly CodedEntry Hypertension = new("I10", "Essential (primary) hypertension", IsPrimary: false);

    [Fact]
    public void Lists_each_code_with_its_title_in_the_coders_order() =>
        VerifiedEpisodeCoding.AsText([Cellulitis, Hypertension], 1000)
            .ShouldBe("L03.1 Cellulitis of other parts of limb; I10 Essential (primary) hypertension");

    [Fact]
    public void Leaves_the_titles_out_when_they_do_not_fit() =>
        VerifiedEpisodeCoding.AsText([Cellulitis, Hypertension], 40).ShouldBe("L03.1; I10");

    [Fact]
    public void Keeps_the_codes_that_fit_from_the_first_when_even_the_codes_alone_do_not() =>
        VerifiedEpisodeCoding.AsText([Cellulitis, Hypertension], 9).ShouldBe("L03.1");

    [Fact]
    public void Is_empty_when_nothing_was_coded() => VerifiedEpisodeCoding.AsText([], 1000).ShouldBeNull();
}
