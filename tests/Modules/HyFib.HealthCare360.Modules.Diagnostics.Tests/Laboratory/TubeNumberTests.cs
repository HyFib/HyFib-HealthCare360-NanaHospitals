using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// Tube numbers (series <c>lab.sample</c>): Nana Hospitals uses no barcodes, so the technician writes the number on the
/// tube and types it on the analyser as its sample id, and the analyser's results come back under it. Every number has
/// exactly 10 digits and no leading zero, and analyser results keep matching it exactly.
/// </summary>
public sealed class TubeNumberTests
{
    private static readonly NumberSeriesDefinition Tubes = DiagnosticsNumberSeries.LabSample;

    private static readonly DateTimeOffset Now = TestData.Now;

    [Fact]
    public void Tube_numbers_are_ten_digits_from_1000000000_shared_by_every_facility_and_locked()
    {
        Tubes.DefaultFormat.ShouldBe("{SEQ:10}");
        Tubes.DefaultReset.ShouldBe(NumberResetPolicy.Never);
        Tubes.FirstNumber.ShouldBe(1_000_000_000L);
        Tubes.PerFacility.ShouldBeFalse("analysers find a sample by its number alone");
        Tubes.IsLocked.ShouldBeTrue();
        Tubes.LockedReason.ShouldNotBeNull().ShouldContain("10-digit");
        NumberFormatRules.FormatProblems(Tubes.DefaultFormat).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0L, "1000000000")]
    [InlineData(523L, "1000000000")]
    [InlineData(1_000_000_041L, "1000000042")]
    [InlineData(9_999_999_998L, "9999999999")]
    public void Every_tube_number_has_ten_digits_and_no_leading_zero(long lastIssued, string next)
    {
        var number = NumberFormat.Render(Tubes.DefaultFormat, NumberFormat.NextNumber(lastIssued, Tubes.FirstNumber), null, Now);

        number.ShouldBe(next);
        number.Length.ShouldBe(10);
        number[0].ShouldNotBe('0');
    }

    [Fact]
    public void The_largest_tube_number_still_has_ten_digits_and_fits_where_numbers_are_kept_and_sent()
    {
        var largest = NumberFormatRules.LargestStartAt(Tubes, Tubes.DefaultFormat).ShouldNotBeNull();

        largest.ShouldBe(9_999_999_999L);
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
        NumberFormatRules.Check(change with { StartAt = 1_200_000_000 }).ShouldBeEmpty("raising it, to carry on from an older system, still works");
    }
}
