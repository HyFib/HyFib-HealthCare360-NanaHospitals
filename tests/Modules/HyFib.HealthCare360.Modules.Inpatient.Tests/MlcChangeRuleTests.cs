using HyFib.HealthCare360.Modules.Inpatient.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// When a stay's medico-legal flag waits for the MLC register (maker-checker, slice 7): marking it, or giving it its
/// number, is at once; clearing it, or changing or removing the number it is linked by, waits.
/// </summary>
public sealed class MlcChangeRuleTests
{
    [Theory]
    [InlineData(false, null, true, null, false)]
    [InlineData(false, null, true, "MLC/1", false)]
    [InlineData(true, null, true, "MLC/1", false)]
    [InlineData(true, "MLC/1", true, "mlc/1", false)]
    [InlineData(true, "MLC/1", false, null, true)]
    [InlineData(true, "MLC/1", true, null, true)]
    [InlineData(true, "MLC/1", true, "MLC/2", true)]
    [InlineData(true, null, false, null, true)]
    public void Setting_the_flag_is_at_once_and_clearing_or_unlinking_it_waits(bool isMlc, string? number, bool askedMlc, string? askedNumber, bool waits) =>
        MlcChangeRule.Waits(new MlcStanding(isMlc, number), new MlcStanding(askedMlc, askedNumber)).ShouldBe(waits);

    [Fact]
    public void The_standing_is_put_in_words()
    {
        new MlcStanding(true, "ER/26/0001").Words.ShouldBe("MLC no. ER/26/0001");
        new MlcStanding(true, null).Words.ShouldBe("MLC (no number yet)");
        new MlcStanding(false, "ER/26/0001").Words.ShouldBe("not a medico-legal case");
    }
}
