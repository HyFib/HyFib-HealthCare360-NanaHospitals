using System.Globalization;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>When a test waits for midnight in India to pass (<see cref="AwayFromMidnightInIndiaAttribute"/>).</summary>
public sealed class AwayFromMidnightInIndiaTests
{
    [Theory]
    [InlineData("2026-11-04T12:00:00+05:30", 0)]
    [InlineData("2026-11-04T23:55:00+05:30", 0)]
    [InlineData("2026-11-04T23:55:01+05:30", 419)]
    [InlineData("2026-11-04T23:59:59+05:30", 121)]
    [InlineData("2026-11-04T18:29:59+00:00", 121)]
    [InlineData("2026-11-05T00:00:00+05:30", 120)]
    [InlineData("2026-11-05T00:01:30+05:30", 30)]
    [InlineData("2026-11-05T00:02:00+05:30", 0)]
    public void A_test_waits_only_near_midnight_in_india_and_then_until_two_minutes_past_it(string at, int seconds) =>
        AwayFromMidnightInIndiaAttribute.WaitAt(DateTimeOffset.Parse(at, CultureInfo.InvariantCulture)).ShouldBe(TimeSpan.FromSeconds(seconds));
}
