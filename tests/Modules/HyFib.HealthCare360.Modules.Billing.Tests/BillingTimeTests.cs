using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class BillingTimeTests
{
    [Theory]
    [InlineData("2026-09-25T18:29:00Z", "2026-09-25")]
    [InlineData("2026-09-25T18:30:00Z", "2026-09-26")]
    [InlineData("2026-09-25T20:00:00Z", "2026-09-26")]
    public void Calendar_days_follow_indian_standard_time(string utc, string expected)
    {
        BillingTime.LocalDate(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_local_day_starts_at_18_30_utc_of_the_previous_day()
    {
        BillingTime.StartOfDay(new DateOnly(2026, 9, 26)).ShouldBe(new DateTimeOffset(2026, 9, 25, 18, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_range_is_half_open_over_whole_local_days()
    {
        var (start, end) = BillingTime.Range(new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 25));

        start.ShouldBe(new DateTimeOffset(2026, 9, 24, 18, 30, 0, TimeSpan.Zero));
        end.ShouldBe(new DateTimeOffset(2026, 9, 25, 18, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Today_and_local_now_use_the_hospital_clock()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 25, 19, 0, 0, TimeSpan.Zero));

        BillingTime.Today(time).ShouldBe(new DateOnly(2026, 9, 26));
        BillingTime.LocalNow(time).Offset.ShouldBe(TimeSpan.FromHours(5.5));
    }
}
