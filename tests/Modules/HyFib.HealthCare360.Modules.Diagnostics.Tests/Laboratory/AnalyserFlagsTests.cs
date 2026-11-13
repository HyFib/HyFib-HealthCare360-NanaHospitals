using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>The analyser's own critical flags (HL7 OBX-8, ASTM R.7), however the gateway passes them on.</summary>
public sealed class AnalyserFlagsTests
{
    [Theory]
    [InlineData("HH")]
    [InlineData("LL")]
    [InlineData("AA")]
    [InlineData("hh")]
    [InlineData("H,HH")]
    [InlineData("W, LL")]
    [InlineData("HH^Critically high^HL70078")]
    public void Panic_and_critically_abnormal_flags_are_critical(string flags) => AnalyserFlags.IsCritical(flags).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N")]
    [InlineData("H")]
    [InlineData("L")]
    [InlineData("A")]
    [InlineData(">")]
    [InlineData("HU")]
    [InlineData("H,L,W")]
    [InlineData("HHH")]
    public void Other_flags_are_not(string? flags) => AnalyserFlags.IsCritical(flags).ShouldBeFalse();
}
