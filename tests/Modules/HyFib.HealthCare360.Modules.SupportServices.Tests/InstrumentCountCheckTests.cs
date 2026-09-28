using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class InstrumentCountCheckTests
{
    private static readonly CountSheetLine[] MinorTray =
    [
        new("Artery forceps", 4),
        new("Needle holder", 1),
        new("Suture scissors", 1),
    ];

    [Fact]
    public void A_complete_tray_passes()
    {
        var check = InstrumentCountCheck.Compare(MinorTray,
            [new CountedItem("Artery forceps", 4, 0), new CountedItem("Needle holder", 1, 0), new CountedItem("Suture scissors", 1, 0)]);

        check.HasDiscrepancy.ShouldBeFalse();
        check.Expected.ShouldBe(6);
        check.Received.ShouldBe(6);
        check.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_instrument_is_reported()
    {
        var check = InstrumentCountCheck.Compare(MinorTray,
            [new CountedItem("Artery forceps", 3, 0), new CountedItem("Needle holder", 1, 0), new CountedItem("Suture scissors", 1, 0)]);

        check.HasDiscrepancy.ShouldBeTrue();
        check.Missing.ShouldBe(1);
        check.Discrepancies.ShouldBe(["Artery forceps: 1 missing"]);
    }

    [Fact]
    public void Instruments_left_out_of_the_count_are_treated_as_missing()
    {
        var check = InstrumentCountCheck.Compare(MinorTray, [new CountedItem("Artery forceps", 4, 0)]);

        check.Missing.ShouldBe(2);
        check.Discrepancies.Count.ShouldBe(2);
    }

    [Fact]
    public void Instruments_not_on_the_count_sheet_are_reported_as_extras()
    {
        var check = InstrumentCountCheck.Compare(MinorTray,
        [
            new CountedItem("Artery forceps", 4, 0), new CountedItem("Needle holder", 1, 0), new CountedItem("Suture scissors", 1, 0),
            new CountedItem("Babcock forceps", 1, 0),
        ]);

        check.Extra.ShouldBe(1);
        check.Discrepancies.ShouldBe(["Babcock forceps: 1 not on the count sheet"]);
    }

    [Fact]
    public void Damaged_instruments_are_a_discrepancy_even_when_the_count_is_right()
    {
        var check = InstrumentCountCheck.Compare(MinorTray,
            [new CountedItem("Artery forceps", 4, 1), new CountedItem("Needle holder", 1, 0), new CountedItem("Suture scissors", 1, 0)]);

        check.Missing.ShouldBe(0);
        check.Damaged.ShouldBe(1);
        check.HasDiscrepancy.ShouldBeTrue();
        check.Discrepancies.ShouldBe(["Artery forceps: 1 damaged"]);
    }

    [Fact]
    public void Names_are_matched_ignoring_case_and_spaces_and_repeated_counts_are_added()
    {
        var check = InstrumentCountCheck.Compare(MinorTray,
        [
            new CountedItem(" artery FORCEPS ", 2, 0), new CountedItem("Artery forceps", 2, 0), new CountedItem("needle holder", 1, 0),
            new CountedItem("SUTURE SCISSORS", 1, 0),
        ]);

        check.HasDiscrepancy.ShouldBeFalse();
    }
}
