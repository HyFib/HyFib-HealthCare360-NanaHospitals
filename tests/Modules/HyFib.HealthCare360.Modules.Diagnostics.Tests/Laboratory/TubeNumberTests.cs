using System.Globalization;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// Tube numbers (series <c>lab.sample</c>): Nana Hospitals uses no barcodes, so the technician writes the number on the
/// tube and types it on the analyser as its sample id, and the analyser's results come back under it. Every number has
/// exactly 10 digits and no leading zero, and ends in a check digit: a number typed with one digit wrong, or two
/// neighbouring digits swapped, is no tube's number, so it never finds the tube drawn just before or after.
/// </summary>
public sealed class TubeNumberTests
{
    private static readonly NumberSeriesDefinition Tubes = DiagnosticsNumberSeries.LabSample;

    private static readonly DateTimeOffset Now = TestData.Now;

    [Fact]
    public void Tube_numbers_are_ten_digits_ending_in_a_check_digit_shared_by_every_facility_and_locked()
    {
        Tubes.DefaultFormat.ShouldBe("{SEQ:9}{CHK}");
        Tubes.DefaultReset.ShouldBe(NumberResetPolicy.Never);
        Tubes.FirstNumber.ShouldBe(100_000_000L);
        Tubes.PerFacility.ShouldBeFalse("analysers find a sample by its number alone");
        Tubes.IsLocked.ShouldBeTrue();
        Tubes.LockedReason.ShouldNotBeNull().ShouldContain("check digit");
        NumberFormatRules.FormatProblems(Tubes.DefaultFormat).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0L, "1000000005")]
    [InlineData(52L, "1000000005")]
    [InlineData(100_001_233L, "1000012342")]
    [InlineData(999_999_998L, "9999999990")]
    public void Every_tube_number_has_ten_digits_no_leading_zero_and_its_check_digit(long lastIssued, string next)
    {
        var number = NumberFormat.Render(Tubes.DefaultFormat, NumberFormat.NextNumber(lastIssued, Tubes.FirstNumber), null, Now);

        number.ShouldBe(next);
        number.Length.ShouldBe(10);
        number[0].ShouldNotBe('0');
        DammCheckDigit.Verifies(number).ShouldBeTrue();
    }

    /// <summary>
    /// L1: tubes for patients A and B are drawn minutes apart. A's tube is run, but its number is typed with one digit
    /// wrong, or with two neighbouring digits swapped. Without a check digit, 1000012345 typed as 1000012346 was B's tube,
    /// and A's blood count went on B's report.
    /// </summary>
    [Fact]
    public void A_tube_number_typed_with_one_digit_wrong_or_two_swapped_is_never_the_next_tubes_or_any_tubes()
    {
        var last = Tubes.FirstNumber + 1_233;
        var a = Number(NumberFormat.NextNumber(last, Tubes.FirstNumber));
        var b = Number(NumberFormat.NextNumber(last + 1, Tubes.FirstNumber));

        Mistyped(a).Where(m => m.Typed == b).Select(m => $"{m.Typed} is {a} {m.How}").ShouldBeEmpty("B's tube must never be found");
        foreach (var (typed, how) in Mistyped(a))
        {
            DammCheckDigit.Verifies(typed).ShouldBeFalse($"{typed} is {a} {how}: its check digit does not match");
        }
    }

    [Fact]
    public void The_largest_tube_number_still_has_ten_digits_and_fits_where_numbers_are_kept_and_sent()
    {
        var largest = NumberFormatRules.LargestStartAt(Tubes, Tubes.DefaultFormat).ShouldNotBeNull();

        largest.ShouldBe(999_999_999L);
        Number(largest).ShouldBe("9999999990");
        NumberFormatRules.LongestNumber(Tubes.DefaultFormat, 0).Total.ShouldBe(10);
        Tubes.MaxLength.ShouldBeGreaterThanOrEqualTo(10, "lab_samples.barcode holds 20 characters");
        NumberFormatRules.LongestNumber(Tubes.DefaultFormat, 0).Total.ShouldBeLessThanOrEqualTo(16, "the Yumizen H500's sample id holds 16 characters");
    }

    [Fact]
    public void An_administrator_cannot_set_a_next_tube_number_that_would_lose_a_digit()
    {
        var change = new NumberFormatChange
        {
            Series = Tubes,
            Format = Tubes.DefaultFormat,
            ResetPolicy = Tubes.DefaultReset,
            StartAt = 1_000,
            CurrentFormat = Tubes.DefaultFormat,
            CurrentResetPolicy = Tubes.DefaultReset,
        };

        NumberFormatRules.Check(change).Keys.ShouldBe([NumberFormatRules.StartAtField]);
        NumberFormatRules.Check(change with { StartAt = 120_000_000 }).ShouldBeEmpty("raising it, to carry on from an older system, still works");
        NumberFormatRules.Check(change with { StartAt = 1_000_000_000 }).Keys.ShouldBe([NumberFormatRules.StartAtField], "11 digits");
    }

    private static string Number(long running) => NumberFormat.Render(Tubes.DefaultFormat, running, null, Now);

    /// <summary>Every way of typing <paramref name="number"/> with one digit wrong, or two neighbouring digits swapped.</summary>
    internal static IEnumerable<(string Typed, string How)> Mistyped(string number)
    {
        for (var place = 0; place < number.Length; place++)
        {
            foreach (var digit in "0123456789".Where(d => d != number[place]))
            {
                yield return (number[..place] + digit + number[(place + 1)..], $"with digit {(place + 1).ToString(CultureInfo.InvariantCulture)} wrong");
            }
        }

        for (var place = 0; place + 1 < number.Length; place++)
        {
            if (number[place] != number[place + 1])
            {
                yield return (number[..place] + number[place + 1] + number[place] + number[(place + 2)..],
                    $"with digits {(place + 1).ToString(CultureInfo.InvariantCulture)} and {(place + 2).ToString(CultureInfo.InvariantCulture)} swapped");
            }
        }
    }
}
