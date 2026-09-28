using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class LabTimeTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    [Theory]
    [InlineData("20260925103015", 2026, 9, 25, 10, 30, 15)]
    [InlineData("202609251030", 2026, 9, 25, 10, 30, 0)]
    [InlineData("2026092510", 2026, 9, 25, 10, 0, 0)]
    [InlineData("20260925", 2026, 9, 25, 0, 0, 0)]
    [InlineData("202609", 2026, 9, 1, 0, 0, 0)]
    [InlineData("2026", 2026, 1, 1, 0, 0, 0)]
    public void Reads_analyser_timestamps_of_any_precision_as_facility_local_time(string value, int year, int month, int day, int hour, int minute, int second) =>
        LabTime.Parse(value, LabSamples.India).ShouldBe(new DateTimeOffset(year, month, day, hour, minute, second, Ist));

    [Fact]
    public void Keeps_fractional_seconds() =>
        LabTime.Parse("20260925103015.25", LabSamples.India).ShouldBe(new DateTimeOffset(2026, 9, 25, 10, 30, 15, 250, Ist));

    [Theory]
    [InlineData("20260925103015+0000", 0)]
    [InlineData("20260925103015-0500", -300)]
    [InlineData("20260925103015.5+0530", 330)]
    public void Honours_an_explicit_offset(string value, int offsetMinutes) =>
        LabTime.Parse(value, LabSamples.India)!.Value.Offset.ShouldBe(TimeSpan.FromMinutes(offsetMinutes));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026092")]
    [InlineData("20261325")]
    [InlineData("2026-09-25")]
    [InlineData("20260925103015+99")]
    [InlineData("abcdefgh")]
    public void Unreadable_values_are_ignored(string? value) => LabTime.Parse(value, LabSamples.India).ShouldBeNull();

    [Fact]
    public void Unknown_zones_fall_back_to_india_standard_time()
    {
        LabTime.ResolveZone("Nowhere/Invalid").BaseUtcOffset.ShouldBe(Ist);
        LabTime.ResolveZone(null).BaseUtcOffset.ShouldBe(Ist);
        LabTime.ResolveZone("UTC").BaseUtcOffset.ShouldBe(TimeSpan.Zero);
    }
}
