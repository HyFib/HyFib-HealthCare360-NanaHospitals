using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// The swab, needle and instrument counts (maker-checker slice 10): counted at the start, as more are opened and before
/// closure; a final count that does not tally opens a discrepancy, and the WHO Sign-out waits until it is settled.
/// </summary>
public sealed class SurgicalCountTests
{
    private static readonly ChecklistInput CountsCorrect = new(SurgicalSafetyChecklist.CountsItem, ChecklistItemState.Done, null);

    [Fact]
    public void The_final_count_is_checked_against_the_initial_count_and_what_was_added()
    {
        var booking = TestData.InProgress();
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Initial, 10)).IsSuccess.ShouldBeTrue();
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Additional, 5)).IsSuccess.ShouldBeTrue();

        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Final, 15)).Value.ShouldBeNull();

        var tally = SurgicalCounts.Tally(booking.Counts).ShouldHaveSingleItem();
        (tally.Expected, tally.Final, tally.Tallies).ShouldBe((15, 15, true));
        booking.CountDiscrepancies.ShouldBeEmpty();
    }

    [Fact]
    public void A_final_count_that_does_not_tally_opens_a_discrepancy_and_holds_the_sign_out()
    {
        var booking = TestData.InProgress();
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Initial, 10)).IsSuccess.ShouldBeTrue();

        var discrepancy = booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Final, 9)).Value.ShouldNotBeNull();

        (discrepancy.Category, discrepancy.Expected, discrepancy.Counted, discrepancy.IsOpen).ShouldBe((CountCategory.Swabs, 10, 9, true));
        var signOut = TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignOut]);
        booking.ConfirmSafety(SafetyPhase.SignOut, signOut, TestData.Staff()).Error!.Code.ShouldBe("surgery.count_discrepancy_open");
        booking.ConfirmSafety(SafetyPhase.SignOut, [new ChecklistInput("procedure_recorded", ChecklistItemState.Done, null)], TestData.Staff())
            .Error!.Code.ShouldBe("surgery.count_discrepancy_open", "nothing of the Sign-out is recorded while a count is open");
        booking.SignOutCompletedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(DiscrepancyResolution.Found)]
    [InlineData(DiscrepancyResolution.XrayNoRetainedItem)]
    public void A_settled_discrepancy_lets_the_sign_out_complete(DiscrepancyResolution resolution)
    {
        var booking = TestData.InProgress();
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Initial, 10)).IsSuccess.ShouldBeTrue();
        var open = booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Final, 9)).Value!;

        var settled = booking.ResolveDiscrepancy(open.Id, resolution, "Found in the kick bucket", "No radio-opaque marker seen", null, TestData.Staff());

        settled.Value.IsOpen.ShouldBeFalse();
        settled.Value.ResolvedByName.ShouldBe("Sr. Anita Joseph");
        booking.ConfirmSafety(SafetyPhase.SignOut, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignOut]), TestData.Staff()).IsSuccess.ShouldBeTrue();
        booking.SignOutCompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Each_way_of_settling_needs_what_it_says()
    {
        var booking = TestData.InProgress();
        booking.RecordCount(TestData.Count(CountCategory.Needles, CountStage.Initial, 4)).IsSuccess.ShouldBeTrue();
        var open = booking.RecordCount(TestData.Count(CountCategory.Needles, CountStage.Final, 3)).Value!;

        booking.ResolveDiscrepancy(open.Id, DiscrepancyResolution.Found, " ", null, null, TestData.Staff()).Error!.Fields!.ShouldContainKey("note");
        booking.ResolveDiscrepancy(open.Id, DiscrepancyResolution.XrayNoRetainedItem, "X-ray in theatre", null, null, TestData.Staff()).Error!.Fields!.ShouldContainKey("xrayResult");
        booking.ResolveDiscrepancy(open.Id, DiscrepancyResolution.RetainedItem, "Needle broke in tissue", null, null, TestData.Staff())
            .Error!.Code.ShouldBe("surgery.discrepancy_incident");

        var retained = booking.ResolveDiscrepancy(open.Id, DiscrepancyResolution.RetainedItem, "Needle broke in tissue", "Fragment seen", (Guid.CreateVersion7(), "NHIR2600001"),
            TestData.Staff());
        retained.Value.IncidentNumber.ShouldBe("NHIR2600001");
        booking.ResolveDiscrepancy(open.Id, DiscrepancyResolution.Found, "Again", null, null, TestData.Staff()).Error!.Code.ShouldBe("surgery.discrepancy_settled");
    }

    [Fact]
    public void Counts_are_confirmed_at_the_sign_out_only_once_every_category_is_counted_at_the_end()
    {
        var booking = TestData.InProgress();
        booking.ConfirmSafety(SafetyPhase.SignOut, [CountsCorrect], TestData.Staff()).Error!.Code.ShouldBe("surgery.counts_missing", "a tick alone is not a count");

        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Initial, 10)).IsSuccess.ShouldBeTrue();
        booking.RecordCount(TestData.Count(CountCategory.Instruments, CountStage.Initial, 40)).IsSuccess.ShouldBeTrue();
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Final, 10)).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignOut, [CountsCorrect], TestData.Staff()).Error!.Code.ShouldBe("surgery.counts_incomplete");

        booking.RecordCount(TestData.Count(CountCategory.Instruments, CountStage.Final, 40)).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignOut, [CountsCorrect], TestData.Staff()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Counts_are_not_applicable_only_when_nothing_was_counted()
    {
        var counted = TestData.CountsTallied(TestData.InProgress());
        var notApplicable = new ChecklistInput(SurgicalSafetyChecklist.CountsItem, ChecklistItemState.NotApplicable, "Nothing to count");

        counted.ConfirmSafety(SafetyPhase.SignOut, [notApplicable], TestData.Staff()).Error!.Code.ShouldBe("surgery.counts_recorded");
        TestData.InProgress().ConfirmSafety(SafetyPhase.SignOut, [notApplicable], TestData.Staff()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Each_category_is_counted_at_the_start_and_at_the_end_once()
    {
        var booking = TestData.ReadyForSignIn();

        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Additional, 5)).Error!.Code.ShouldBe("surgery.count_no_initial");
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Initial, 10)).IsSuccess.ShouldBeTrue();
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Initial, 12)).Error!.Code.ShouldBe("surgery.count_recorded");
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Final, 10)).Error!.Code.ShouldBe("surgery.count_not_started");
        booking.RecordCount(TestData.Count(CountCategory.Swabs, CountStage.Additional, 0)).Error!.Fields!.ShouldContainKey("number");
    }

    [Fact]
    public void Counts_close_with_the_sign_out()
    {
        var booking = TestData.CountsTallied(TestData.InProgress());
        booking.ConfirmSafety(SafetyPhase.SignOut, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignOut]), TestData.Staff()).IsSuccess.ShouldBeTrue();

        booking.RecordCount(TestData.Count(CountCategory.Needles, CountStage.Initial, 2)).Error!.Code.ShouldBe("surgery.counts_closed");
        TestData.NewBooking().RecordCount(TestData.Count(CountCategory.Needles, CountStage.Initial, 2)).Error!.Code.ShouldBe("surgery.counts_closed");
    }
}
