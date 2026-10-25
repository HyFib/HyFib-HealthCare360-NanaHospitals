using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>The GSTIN: its state, PAN, "Z" and the GSTN check character catch a mistyped number.</summary>
public sealed class GstinTests
{
    [Theory]
    [InlineData("27AAPFU0939F1ZV", "27")]
    [InlineData("29aagcb7383j1z4", "29")]
    [InlineData(" 33AAACH7409R1Z8 ", "33")]
    public void A_valid_gstin_gives_its_state(string gstin, string state)
    {
        Gstin.IsValid(gstin).ShouldBeTrue();
        Gstin.StateCode(gstin).ShouldBe(state);
    }

    [Theory]
    [InlineData("27AAPFU0939F1YV")]
    [InlineData("99AAPFU0939F1ZV")]
    [InlineData("27AAPF00939F1ZV")]
    [InlineData("27AAPFU0939F1Z")]
    [InlineData("")]
    [InlineData(null)]
    public void Something_that_is_not_a_gstin_is_refused_and_has_no_state(string? gstin)
    {
        Gstin.IsValid(gstin).ShouldBeFalse();
        Gstin.StateCode(gstin).ShouldBeNull();
    }

    [Fact]
    public void A_wrong_check_character_is_refused_for_a_new_number_but_keeps_its_state_for_one_saved_before()
    {
        Gstin.IsValid("27AAPFU0939F1ZW").ShouldBeFalse();
        Gstin.IsWellFormed("27AAPFU0939F1ZW").ShouldBeTrue();
        Gstin.StateCode("27AAPFU0939F1ZW").ShouldBe("27");
    }

    [Fact]
    public void The_check_character_is_computed_as_gstn_does() => Gstin.CheckCharacter("27AAPFU0939F1Z").ShouldBe('V');
}
