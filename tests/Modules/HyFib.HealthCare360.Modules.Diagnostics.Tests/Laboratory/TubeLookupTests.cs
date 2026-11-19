using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Seed;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Numbering;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// Which tube a number typed by staff or sent by an analyser finds (L1): the rule the analyser sink, its host queries,
/// receipt at the lab counter and the lab screens' look-up and search all apply before a tube is looked up. A number whose
/// check digit does not match finds no tube, so A's results never go on B's tube. A tube numbered before tube numbers had
/// a check digit is still found by its exact number, unless that number is also a new tube's number typed wrong; no tube
/// made since is found by a number that does not check.
/// </summary>
public sealed class TubeLookupTests
{
    private static readonly DateTimeOffset Switched = Now.AddDays(-30);

    private static readonly LabSample A = Tube(Number(100_001_234), Now.AddMinutes(-12));
    private static readonly LabSample B = Tube(Number(100_001_235), Now.AddMinutes(-5));

    /// <summary>A tube numbered before the check digit, by the old 10-digit series: its number does not check.</summary>
    private static readonly LabSample Old = Tube("1000000123", Switched.AddDays(-20));

    /// <summary>The last tube numbered before the check digit: A's number typed with its last digit wrong gives its number.</summary>
    private static readonly LabSample LastOld = Tube("1000012346", Switched.AddDays(-2));

    private static readonly LabSample[] Tubes = [A, B, Old, LastOld];

    [Fact]
    public void A_tube_number_finds_its_tube_and_no_other()
    {
        A.Barcode.ShouldBe("1000012342");
        B.Barcode.ShouldBe("1000012350");

        Find(A.Barcode, Switched).ShouldBe([A]);
        Find(B.Barcode, Switched).ShouldBe([B]);
        Find(B.Barcode, since: null).ShouldBe([B]);
    }

    /// <summary>
    /// A's tube is run, but its number is typed on the analyser with one digit wrong, or with two neighbouring digits
    /// swapped: the sink finds no tube, so nothing goes on B's tube, nor on any other.
    /// </summary>
    [Fact]
    public void A_number_typed_with_one_digit_wrong_or_two_swapped_finds_no_tube_and_never_the_neighbours()
    {
        foreach (var (typed, how) in TubeNumberTests.Mistyped(A.Barcode))
        {
            Find(typed, Switched).ShouldBeEmpty($"{typed} is {A.Barcode} {how}");
            TubeNumbers.IsMistyped(typed).ShouldBeTrue($"{typed} is {A.Barcode} {how}");
        }
    }

    [Fact]
    public void The_typo_that_found_the_neighbours_tube_before_the_check_digit_is_now_refused_with_why()
    {
        // 100001234 and 100001235 differ in their ninth digit: typing 5 for 4 kept A's check digit, which is not B's.
        var typed = A.Barcode[..8] + "5" + A.Barcode[9];

        typed.ShouldBe("1000012352");
        typed.ShouldNotBe(B.Barcode);
        Find(typed, Switched).ShouldBeEmpty();
        TubeNumbers.MistypedMessage(typed).ShouldBe(
            "1000012352 is not a tube number: its last digit, the check digit, does not match the others. A digit was typed wrong, " +
            "or two were swapped. Check the number written on the tube and type it again.");
        TubeNumbers.MistypedByAnalyser("H500", typed).ShouldBe(
            "H500 sent sample 1000012352, which is not a tube number: its last digit, the check digit, does not match the others, " +
            "so a digit was typed wrong on the analyser or two were swapped. No tube was changed. Find the tube that was run and put " +
            "these results on its tests.");
    }

    [Fact]
    public void A_tube_numbered_before_the_check_digit_is_found_by_its_exact_number()
    {
        TubeNumbers.Verifies(Old.Barcode).ShouldBeFalse("the old series had no check digit");

        Find(Old.Barcode, Switched).ShouldBe([Old]);
        Find(Old.Barcode, since: null).ShouldBeEmpty("a database whose tubes all have a check digit has no such tube");
    }

    /// <summary>
    /// New numbers start just above the old ones, so a new tube's number typed with one digit wrong can be an old tube's
    /// number exactly: 1000012342 with its last digit typed 6 is the last old tube's. That number finds neither tube.
    /// </summary>
    [Fact]
    public void An_old_tubes_number_that_a_new_tubes_number_typed_wrong_also_gives_finds_neither()
    {
        TubeNumbers.Near(LastOld.Barcode).ShouldContain(A.Barcode);

        Find(LastOld.Barcode, Switched).ShouldBeEmpty();
        TubeNumbers.IsMistyped(LastOld.Barcode).ShouldBeTrue("the lab is told to check the number on the tube");
        TubeNumbers.Near(Old.Barcode).ShouldNotContain(A.Barcode);
        TubeNumbers.Near(Old.Barcode).ShouldNotContain(B.Barcode);
    }

    [Fact]
    public void A_tube_made_since_the_check_digit_is_never_found_by_a_number_that_does_not_check()
    {
        // Not one the series makes; were one there (put in by hand), a number that does not check would still not find it.
        LabSample[] tubes = [Tube("1000000123", Switched.AddMinutes(1))];

        tubes.AsQueryable().Where(TubeNumbers.Matching("1000000123", Switched, tubes.AsQueryable())).ShouldBeEmpty();
    }

    [Fact]
    public void The_numbers_a_number_can_be_a_mistyping_of_all_check_and_are_one_slip_away()
    {
        var near = TubeNumbers.Near("1000000123");

        near.ShouldBe(
        [
            "2000000123", "1100000123", "1070000123", "1001000123", "1000040123", "1000700123", "1000003123", "1000000923", "1000000193",
            "1000000129", "1000000132",
        ], ignoreOrder: true);
        near.ShouldAllBe(n => TubeNumbers.Verifies(n));
        TubeNumbers.Near("QC-LEVEL-1").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("1000012342", false)]
    [InlineData("1000012352", true)]
    [InlineData("1000012324", true)]
    [InlineData("100001234", false)]
    [InlineData("10000123420", false)]
    [InlineData("QC-LEVEL-1", false)]
    [InlineData("", false)]
    public void Only_ten_digits_whose_check_digit_does_not_match_are_a_mistyped_tube_number(string number, bool mistyped) =>
        TubeNumbers.IsMistyped(number).ShouldBe(mistyped);

    [Fact]
    public void A_sample_id_that_is_not_digits_is_no_tube_number()
    {
        TubeNumbers.Verifies("QC-LEVEL-1").ShouldBeFalse();
        Find("QC-LEVEL-1", Switched).ShouldBeEmpty();
    }

    /// <summary>
    /// The seeder brings a database from before to the check digit: its counter, counted in tens of the old numbers, goes on
    /// above every tube numbered before (L1). A database with 9-digit tubes goes on from the first tube number.
    /// </summary>
    [Theory]
    [InlineData(1_000_012_346L, "{SEQ:10}", 100_001_234L, "1000012350")]
    [InlineData(1_000_000_000L, "{SEQ:10}", 100_000_000L, "1000000018")]
    [InlineData(523L, "{SEQ:9}", 52L, "1000000005")]
    [InlineData(0L, "{SEQ:10}", 0L, "1000000005")]
    [InlineData(100_001_234L, "{SEQ:9}{CHK}", 100_001_234L, "1000012350")]
    public void The_seeder_counts_an_old_tube_counter_in_tens_so_the_next_tube_is_above_every_old_one(
        long before, string format, long after, string next)
    {
        var series = DiagnosticsNumberSeries.LabSample;
        var counter = DiagnosticsSeeder.TubeCounterAfterUpgrade(before, format);

        counter.ShouldBe(after);
        NumberFormat.Render(series.DefaultFormat, NumberFormat.NextNumber(counter, series.FirstNumber), null, Now).ShouldBe(next);
        long.Parse(next, System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(before);
    }

    [Fact]
    public void A_mistyped_tube_numbers_result_put_on_a_tube_by_hand_tells_the_pathologist()
    {
        var kept = new UnmatchedLabResult
        {
            FacilityId = Guid.CreateVersion7(),
            InstrumentCode = "H500",
            SampleBarcode = "1000012352",
            ChannelCode = "HGB",
            Value = "13.9",
            ReceivedAt = Now,
            Reason = UnmatchedResultReason.MistypedTubeNumber,
            Detail = TubeNumbers.MistypedByAnalyser("H500", "1000012352"),
        };

        UnmatchedResultService.HeldReasons(kept).ShouldBe(
        [
            UnmatchedResultService.AssignedReason,
            "The analyser sent a tube number whose check digit does not match, so a person chose the tube: 1000012352 was typed on the analyser.",
        ]);
    }

    private static List<LabSample> Find(string number, DateTimeOffset? since) =>
        [.. Tubes.AsQueryable().Where(TubeNumbers.Matching(number, since, Tubes.AsQueryable()))];

    private static string Number(long running) => NumberFormat.Render(DiagnosticsNumberSeries.LabSample.DefaultFormat, running, null, Now);

    private static LabSample Tube(string number, DateTimeOffset createdAt)
    {
        var tube = Sample(SampleStatus.Collected, LabSection.Hematology);
        tube.Barcode = number;
        tube.CreatedAt = createdAt;
        return tube;
    }
}
