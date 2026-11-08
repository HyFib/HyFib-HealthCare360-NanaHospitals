using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// When cancelling a visit is still the desk's alone (MAKER-CHECKER.md, slice 15): until the doctor has started it. The
/// consultation under way, or any work on the visit's record, makes it wait for a second person.
/// </summary>
public sealed class VisitCancelRuleTests
{
    private static readonly Guid Doctor = Guid.CreateVersion7();

    [Fact]
    public void A_visit_waiting_with_nothing_recorded_is_cancelled_at_once()
    {
        VisitService.CancelNeedsApproval(VisitStatus.Open, VisitClinicalWork.None).ShouldBeFalse();
    }

    [Fact]
    public void A_consultation_under_way_waits_even_with_nothing_written_yet()
    {
        VisitService.CancelNeedsApproval(VisitStatus.InProgress, VisitClinicalWork.None).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, 0, 1)]
    public void Any_encounter_note_order_line_or_prescription_makes_it_wait(int encounters, int notes, int lines, int prescriptions)
    {
        var work = new VisitClinicalWork(encounters, 0, notes, lines, prescriptions, [Doctor]);

        VisitService.CancelNeedsApproval(VisitStatus.Open, work).ShouldBeTrue();
    }

    [Fact]
    public void The_approver_reads_the_work_in_words()
    {
        new VisitClinicalWork(1, 0, 2, 1, 1, [Doctor]).Words().ShouldBe("1 encounter (not signed), 2 notes, 1 order line, 1 e-prescription");
        new VisitClinicalWork(2, 2, 0, 3, 0, [Doctor]).Words().ShouldBe("2 encounters (signed), 3 order lines");
        new VisitClinicalWork(2, 1, 0, 0, 0, [Doctor]).Words().ShouldBe("2 encounters (1 signed)");
        VisitClinicalWork.None.Words().ShouldBe("Nothing recorded yet");
    }
}
