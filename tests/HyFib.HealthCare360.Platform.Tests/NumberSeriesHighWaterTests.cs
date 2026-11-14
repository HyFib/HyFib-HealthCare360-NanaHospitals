using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The rules of the number series' high-water mark: a restore puts every counter back to where it was when the backup
/// was made, and start-up moves each one behind the mark past it, so the UHIDs, bills and receipts issued after the backup
/// are not issued again. The mark itself follows the database, but never goes back with it.
/// </summary>
public sealed class NumberSeriesHighWaterTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateTimeOffset November14 = new(2026, 11, 14, 10, 0, 0, Ist);
    private static readonly DateTimeOffset January5 = new(2027, 1, 5, 10, 0, 0, Ist);
    private static readonly Guid Main = Guid.Parse("0193a0c1-0000-7000-8000-000000000001");
    private static readonly Guid Branch = Guid.Parse("0193a0c1-0000-7000-8000-000000000002");

    [Fact]
    public void A_series_the_restore_put_back_is_raised_past_the_mark_by_the_margin()
    {
        var raise = NumberSeriesHighWater.Raises([Uhid(1200)], [Uhid(1234)], November14).ShouldHaveSingleItem();

        raise.Before.ShouldBe(Uhid(1200));
        raise.Mark.ShouldBe(Uhid(1234));
        raise.Period.ShouldBeNull("UHIDs never start again from 1");
        raise.Value.ShouldBe(1234 + NumberSeriesHighWater.Margin, "the last number the mark records, and room for those issued after it was written");
    }

    [Fact]
    public void A_series_at_or_past_the_mark_is_left_as_it_is()
    {
        NumberSeriesHighWater.Raises([Uhid(1234)], [Uhid(1234)], November14).ShouldBeEmpty();
        NumberSeriesHighWater.Raises([Uhid(1300)], [Uhid(1234)], November14).ShouldBeEmpty("numbers issued since the mark was written");
        NumberSeriesHighWater.Raises([Uhid(1200), OpBill(Main, "2026", 40)], [OpBill(Main, "2026", 40)], November14)
            .ShouldBeEmpty("a series the mark does not know is left as it is");
    }

    [Fact]
    public void Each_facility_is_compared_with_its_own_mark()
    {
        var raise = NumberSeriesHighWater.Raises(
                [OpBill(Main, "2026", 400), OpBill(Branch, "2026", 90)],
                [OpBill(Main, "2026", 480), OpBill(Branch, "2026", 60)],
                November14)
            .ShouldHaveSingleItem("the branch issued more after the mark than before the backup");

        raise.Mark.FacilityId.ShouldBe(Main);
        raise.Value.ShouldBe(480 + NumberSeriesHighWater.Margin);
    }

    [Fact]
    public void A_yearly_series_restored_to_last_year_moves_to_the_year_of_the_mark()
    {
        // The backup was made in December; the failure came in January, after the new year's first bills.
        var raise = NumberSeriesHighWater.Raises([OpBill(Main, "2026", 9000)], [OpBill(Main, "2027", 35)], January5).ShouldHaveSingleItem();

        raise.Period.ShouldBe("2027", "otherwise the next bill would be 2027's first, issued already");
        raise.Value.ShouldBe(35 + NumberSeriesHighWater.Margin);
    }

    [Fact]
    public void Numbers_the_mark_has_only_in_an_earlier_period_raise_nothing()
    {
        // Restored in the new year: the series starts again from 1 anyway, with the year in its numbers.
        NumberSeriesHighWater.Raises([OpBill(Main, "2026", 30)], [OpBill(Main, "2026", 9000)], January5).ShouldBeEmpty();
    }

    [Fact]
    public void A_mark_of_a_later_period_than_the_clock_shows_is_not_applied()
    {
        // Moving the series into 2027 would start 2026's numbers again from 1 on the next bill.
        NumberSeriesHighWater.Raises([OpBill(Main, "2026", 9000)], [OpBill(Main, "2027", 5)], November14).ShouldBeEmpty();
    }

    [Fact]
    public void A_daily_series_restored_to_yesterday_continues_past_the_numbers_of_today()
    {
        var token = new NumberSeriesMark(null, "token:4f2c", "20261113", 80, NumberResetPolicy.Daily, "{SEQ}");
        var raise = NumberSeriesHighWater.Raises([token], [token with { Period = "20261114", Value = 40 }], November14).ShouldHaveSingleItem();

        raise.Period.ShouldBe("20261114");
        raise.Value.ShouldBe(40 + NumberSeriesHighWater.Margin);
    }

    [Fact]
    public void The_margin_is_added_to_the_last_number_the_mark_records()
    {
        NumberSeriesHighWater.Raises([Uhid(1200)], [Uhid(1234)], November14, margin: 25).ShouldHaveSingleItem().Value.ShouldBe(1259);
        NumberSeriesHighWater.Raises([Uhid(1200)], [Uhid(1234)], November14, margin: 0).ShouldHaveSingleItem().Value.ShouldBe(1234);
    }

    [Fact]
    public void A_series_the_database_lost_is_made_again_past_the_mark()
    {
        // Its first bill was issued after the backup was made, so the restored database has no row for it.
        var raise = NumberSeriesHighWater.Raises([], [OpBill(Main, "2026", 40)], November14).ShouldHaveSingleItem();
        raise.Before.ShouldBeNull();
        raise.Period.ShouldBe("2026");
        raise.Value.ShouldBe(40 + NumberSeriesHighWater.Margin);

        NumberSeriesHighWater.Raises([], [OpBill(Main, "2025", 40)], November14).ShouldBeEmpty("last year's bills are not issued again this year");
        NumberSeriesHighWater.Raises([], [Uhid(0)], November14).ShouldBeEmpty("a series set up before its first number");
    }

    [Fact]
    public void A_reset_changed_after_the_backup_still_counts_the_numbers_issued_under_the_other()
    {
        // The backup has the bills counted every year; after it, they were counted every month.
        var raise = NumberSeriesHighWater.Raises(
            [OpBill(Main, "2026", 450)],
            [OpBill(Main, "202611", 520) with { ResetPolicy = NumberResetPolicy.Monthly }],
            November14).ShouldHaveSingleItem();
        raise.Period.ShouldBe("2026", "the series counts as the database has it");
        raise.Value.ShouldBe(520 + NumberSeriesHighWater.Margin);

        NumberSeriesHighWater.Raises(
                [OpBill(Main, "2026", 450)],
                [OpBill(Main, "202610", 520) with { ResetPolicy = NumberResetPolicy.Monthly }],
                November14)
            .ShouldHaveSingleItem("October is in this year: its numbers may be among this year's")
            .Value.ShouldBe(620);
        NumberSeriesHighWater.Raises(
                [OpBill(Main, "2026", 450)],
                [OpBill(Main, "202512", 520) with { ResetPolicy = NumberResetPolicy.Monthly }],
                November14)
            .ShouldBeEmpty("last December's numbers are not this year's");
    }

    [Fact]
    public void The_mark_follows_the_database_but_never_goes_back_with_it()
    {
        var merged = NumberSeriesHighWater.Merge(
            [Uhid(1234), OpBill(Main, "2026", 480)],
            [Uhid(1300), OpBill(Main, "2026", 400)],
            November14);

        merged.ShouldBe([OpBill(Main, "2026", 480), Uhid(1300)], "the bills were restored while the server ran: start-up raises them past 480 when it next starts");
    }

    [Fact]
    public void A_series_the_database_lost_stays_in_the_mark_while_it_protects_a_number()
    {
        NumberSeriesHighWater.Merge([OpBill(Main, "2026", 40), OpBill(Branch, "2025", 70)], [], November14)
            .ShouldBe([OpBill(Main, "2026", 40)], "last year's bills of the branch are not issued again this year");
    }

    [Fact]
    public void A_new_period_in_the_database_replaces_the_mark_and_a_later_one_in_the_mark_is_kept()
    {
        NumberSeriesHighWater.Merge([OpBill(Main, "2026", 9000)], [OpBill(Main, "2027", 3)], January5).ShouldBe([OpBill(Main, "2027", 3)]);
        NumberSeriesHighWater.Merge([OpBill(Main, "2027", 5)], [OpBill(Main, "2026", 9000)], November14)
            .ShouldBe([OpBill(Main, "2027", 5)], "numbers issued in 2027 while the clock was ahead are issued again in 2027 unless the mark keeps them");
    }

    [Fact]
    public void Periods_overlap_where_one_is_part_of_the_other()
    {
        var yearly = OpBill(Main, "2026", 500);
        NumberSeriesHighWater.LastIssuedIn(yearly, "2026").ShouldBe(500);
        NumberSeriesHighWater.LastIssuedIn(yearly, "202611").ShouldBe(500);
        NumberSeriesHighWater.LastIssuedIn(yearly, "20261114").ShouldBe(500);
        NumberSeriesHighWater.LastIssuedIn(yearly, null).ShouldBe(500, "a series that never starts again counts every year's numbers");
        NumberSeriesHighWater.LastIssuedIn(yearly, "2027").ShouldBe(0);
        NumberSeriesHighWater.LastIssuedIn(Uhid(77), "2026").ShouldBe(77);
    }

    private static NumberSeriesMark Uhid(long value) => new(null, "uhid", null, value, NumberResetPolicy.Never, "{FAC}{YY}{SEQ:6}");

    private static NumberSeriesMark OpBill(Guid facility, string period, long value) =>
        new(facility, "billing.bill.op", period, value, NumberResetPolicy.Yearly, "{FAC}OB{YY}{SEQ:6}");
}
