using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// How a format makes a number, and which period a counter counts in: what the next number of a series is, and why a
/// change of format or reset never issues a number twice.
/// </summary>
public sealed class NumberFormatTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateTimeOffset October2 = new(2026, 10, 2, 11, 15, 0, Ist);

    [Theory]
    [InlineData("{FAC}OB{YY}{SEQ:7}", 123, "NHOB260000123")]
    [InlineData("{FAC}/OP/{YYYY}/{SEQ:5}", 42, "NH/OP/2026/00042")]
    [InlineData("VP{YY}{MM}{DD}-{SEQ:4}", 7, "VP261002-0007")]
    [InlineData("{SEQ:9}", 1, "000000001")]
    [InlineData("{FAC}{SEQ}", 1234567, "NH1234567")]
    public void A_format_makes_the_number_from_its_parts(string format, long value, string expected) =>
        NumberFormat.Render(format, value, "NH", October2).ShouldBe(expected);

    [Fact]
    public void A_running_number_outgrows_its_padding_rather_than_being_cut() =>
        NumberFormat.Render("{FAC}AS{SEQ:3}", 12345, "NH", October2).ShouldBe("NHAS12345");

    [Fact]
    public void A_padding_the_rules_refuse_is_capped_rather_than_built()
    {
        // Saved before the rules existed: padding 999999999 digits would exhaust the server's memory.
        NumberFormat.Render("{FAC}AS{SEQ:999999999}", 42, "NH", October2).ShouldBe("NHAS000000000042");
        NumberFormat.Render("{FAC}AS{SEQ:13}", 42, "NH", October2).ShouldBe("NHAS000000000042");
    }

    [Fact]
    public void Anything_a_format_may_not_contain_is_written_as_it_is() =>
        NumberFormat.Render("{FAC}-{XYZ}-{SEQ:2}-{YY", 5, "NH", October2).ShouldBe("NH-{XYZ}-05-{YY");

    [Fact]
    public void Without_a_facility_code_the_facility_part_is_empty() =>
        NumberFormat.Render("{FAC}DN{SEQ:6}", 9, null, October2).ShouldBe("DN000009");

    [Fact]
    public void A_format_is_split_into_fixed_text_and_parts_in_braces()
    {
        var parts = NumberFormat.Parse("{FAC}/OP{YY}{SEQ:6}{seq}{SEQ:x}{SEQ:123}");

        parts.Select(p => p.Kind).ShouldBe(
        [
            NumberFormatPartKind.FacilityCode, NumberFormatPartKind.Text, NumberFormatPartKind.ShortYear, NumberFormatPartKind.Sequence,
            NumberFormatPartKind.Unknown, NumberFormatPartKind.Unknown, NumberFormatPartKind.Sequence,
        ]);
        parts[1].Text.ShouldBe("/OP");
        parts[3].Width.ShouldBe(6);
        parts[6].Width.ShouldBe(123, "the rules refuse a width over 12; the parser keeps what was written");
        NumberFormat.Parse("AB{SEQ").ShouldBe([new NumberFormatPart(NumberFormatPartKind.Text, "AB"), new NumberFormatPart(NumberFormatPartKind.Unclosed, "{SEQ")]);
    }

    [Theory]
    [InlineData(NumberResetPolicy.Never, null)]
    [InlineData(NumberResetPolicy.Yearly, "2026")]
    [InlineData(NumberResetPolicy.Monthly, "202610")]
    [InlineData(NumberResetPolicy.Daily, "20261002")]
    public void A_counter_counts_in_the_period_of_its_reset(NumberResetPolicy policy, string? period) =>
        NumberFormat.PeriodFor(policy, October2).ShouldBe(period);

    [Fact]
    public void Periods_follow_the_hospitals_calendar_not_utc()
    {
        // 20:00 UTC on 1 October is 01:30 on 2 October in India: a daily series has already started again.
        var local = NumberFormat.ToHospitalTime(new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.Zero));

        NumberFormat.PeriodFor(NumberResetPolicy.Daily, local).ShouldBe("20261002");
    }

    [Theory]
    [InlineData(NumberResetPolicy.Yearly, "2026", 41)]
    [InlineData(NumberResetPolicy.Yearly, "2025", 0)]
    [InlineData(NumberResetPolicy.Monthly, "202609", 0)]
    [InlineData(NumberResetPolicy.Never, null, 41)]
    [InlineData(NumberResetPolicy.Never, "2026", 0)]
    public void The_last_number_counts_only_in_the_current_period(NumberResetPolicy policy, string? period, long expected) =>
        NumberFormat.LastIssued(41, period, policy, October2).ShouldBe(expected);

    [Fact]
    public void A_new_next_number_counts_from_there_in_the_current_period()
    {
        var counter = NumberFormat.CounterAfterChange(new NumberCounter(41, "2026"), NumberResetPolicy.Yearly, NumberResetPolicy.Yearly, startAt: 5001, October2);

        counter.ShouldBe(new NumberCounter(5000, "2026"));
        Allocate(ref counter, NumberResetPolicy.Yearly, October2).ShouldBe(5001);
    }

    [Fact]
    public void A_changed_format_alone_leaves_the_counter_as_it_is() =>
        NumberFormat.CounterAfterChange(new NumberCounter(41, "2025"), NumberResetPolicy.Yearly, NumberResetPolicy.Yearly, null, October2)
            .ShouldBe(new NumberCounter(41, "2025"));

    [Fact]
    public void Restarting_every_month_instead_of_every_year_goes_on_counting_until_the_month_ends()
    {
        // 500 OP bills this year, then the format becomes {FAC}OB{YY}{MM}{SEQ:4} and numbers restart monthly.
        var counter = NumberFormat.CounterAfterChange(new NumberCounter(500, "2026"), NumberResetPolicy.Yearly, NumberResetPolicy.Monthly, null, October2);

        Allocate(ref counter, NumberResetPolicy.Monthly, October2).ShouldBe(501, "number 1 of October may have been issued already this year");
        Allocate(ref counter, NumberResetPolicy.Monthly, October2.AddDays(29)).ShouldBe(502);
        Allocate(ref counter, NumberResetPolicy.Monthly, new DateTimeOffset(2026, 11, 1, 9, 0, 0, Ist)).ShouldBe(1, "a new month starts again");
    }

    [Fact]
    public void A_new_reset_after_a_quiet_period_starts_from_1_as_the_old_one_would_have()
    {
        // The last appointment was booked last year: this year's count is 0 either way.
        var counter = NumberFormat.CounterAfterChange(new NumberCounter(9000, "2025"), NumberResetPolicy.Yearly, NumberResetPolicy.Monthly, null, October2);

        counter.ShouldBe(new NumberCounter(0, "202610"));
    }

    [Theory]
    [InlineData(NumberResetPolicy.Never, null, NumberResetPolicy.Yearly, "2026")]
    [InlineData(NumberResetPolicy.Yearly, "2026", NumberResetPolicy.Never, null)]
    [InlineData(NumberResetPolicy.Daily, "20261002", NumberResetPolicy.Monthly, "202610")]
    public void A_new_reset_keeps_the_count_of_the_current_period(NumberResetPolicy from, string? period, NumberResetPolicy to, string? newPeriod)
    {
        var counter = NumberFormat.CounterAfterChange(new NumberCounter(77, period), from, to, null, October2);

        counter.ShouldBe(new NumberCounter(77, newPeriod));
        Allocate(ref counter, to, October2).ShouldBe(78);
    }

    /// <summary>What <c>INumberSeries.NextAsync</c> does to the row: the next value in this period, or 1 in a new one.</summary>
    private static long Allocate(ref NumberCounter counter, NumberResetPolicy policy, DateTimeOffset localNow)
    {
        var period = NumberFormat.PeriodFor(policy, localNow);
        counter = new NumberCounter(string.Equals(counter.Period, period, StringComparison.Ordinal) ? counter.Value + 1 : 1, period);
        return counter.Value;
    }
}
