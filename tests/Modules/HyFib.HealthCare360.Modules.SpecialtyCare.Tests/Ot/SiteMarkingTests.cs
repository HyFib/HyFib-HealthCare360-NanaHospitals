using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// Site marking (maker-checker slice 10): with a side or a level booked, "site marked" is never not applicable, and the pre-op
/// mark is recorded by the surgeon who made it.
/// </summary>
public sealed class SiteMarkingTests
{
    private static readonly ChecklistInput Marked = new(SiteMarking.Item, ChecklistItemState.Done, null);
    private static readonly ChecklistInput NotMarked = new(SiteMarking.Item, ChecklistItemState.NotApplicable, "Not needed");

    private static SurgeryBooking Sided(Laterality laterality = Laterality.Left, string? level = null)
    {
        var booking = TestData.Scheduled();
        (booking.Laterality, booking.SiteLevel) = (laterality, level);
        booking.ReceiveInPreOp(TestData.Now).IsSuccess.ShouldBeTrue();
        return booking;
    }

    [Theory]
    [InlineData(Laterality.Left, null)]
    [InlineData(Laterality.Right, null)]
    [InlineData(Laterality.Bilateral, null)]
    [InlineData(Laterality.NotApplicable, "L4–L5")]
    public void Site_marking_is_not_applicable_never_when_a_side_or_level_is_booked(Laterality laterality, string? level)
    {
        var booking = Sided(laterality, level);

        booking.RecordPreOp([NotMarked], TestData.Staff(), canMarkSite: true).Error!.Code.ShouldBe("checklist.site_marking_required");
        booking.RecordPreOp([Marked], TestData.Staff(name: "Dr. Rao"), canMarkSite: true).IsSuccess.ShouldBeTrue();
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff()).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, [NotMarked], TestData.Staff()).Error!.Code.ShouldBe("checklist.site_marking_required");
    }

    [Fact]
    public void Not_applicable_stays_for_a_midline_procedure()
    {
        var booking = Sided(Laterality.NotApplicable);

        booking.RecordPreOp([NotMarked with { Remarks = "Midline laparotomy" }], TestData.Staff()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_pre_op_mark_is_recorded_by_a_surgeon_and_names_them()
    {
        var booking = Sided();

        booking.RecordPreOp([Marked], TestData.Staff(), canMarkSite: false).Error!.Code.ShouldBe("checklist.site_marked_by_surgeon");
        booking.RecordPreOp([Marked], TestData.Staff(name: "Dr. Rao"), canMarkSite: true).IsSuccess.ShouldBeTrue();

        // The ward nurse then completes the checklist, sending it whole: the mark keeps the surgeon's name.
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(name: "Sr. Anita Joseph")).IsSuccess.ShouldBeTrue();
        booking.PreOpChecks.Single(e => e.Key == SiteMarking.Item).RecordedByName.ShouldBe("Dr. Rao");
    }

    [Fact]
    public void A_mark_on_the_old_side_is_redone_when_the_side_changes()
    {
        var booking = Sided(Laterality.Left);
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(name: "Dr. Rao"), canMarkSite: true).IsSuccess.ShouldBeTrue();
        var before = booking.Site;

        booking.Laterality = Laterality.Right;
        booking.ResetSiteMarkingIfMoved(before);

        booking.PreOpChecks.Single(e => e.Key == SiteMarking.Item).State.ShouldBe(ChecklistItemState.Pending);
        booking.ConfirmSafety(SafetyPhase.SignIn, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn]), TestData.Staff())
            .Error!.Code.ShouldBe("surgery.preop_incomplete");
    }

    [Fact]
    public void The_sign_in_confirms_a_mark_the_pre_op_checklist_recorded()
    {
        var booking = Sided(Laterality.NotApplicable);
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items).Select(i => i.Key == SiteMarking.Item ? NotMarked with { Remarks = "Midline" } : i).ToList(),
            TestData.Staff()).IsSuccess.ShouldBeTrue();

        // A level is written in afterwards, past the pre-op mark that said not applicable (a booking edited outside the plan).
        booking.SiteLevel = "T12";

        booking.ConfirmSafety(SafetyPhase.SignIn, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn]), TestData.Staff())
            .Error!.Code.ShouldBe("surgery.site_not_marked");
    }
}
