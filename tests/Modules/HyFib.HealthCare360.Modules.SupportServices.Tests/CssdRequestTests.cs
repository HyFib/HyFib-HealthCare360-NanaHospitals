using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>
/// A department's request for sterile sets (CSSD-02): the packs issued against it fill its lines, so it goes from open to
/// part issued to issued; a pack of a set it does not ask for, or one more than a line asks for, is not counted. It can be
/// cancelled while still open or part issued, never once issued or cancelled.
/// </summary>
public sealed class CssdRequestTests
{
    private static readonly Guid MajorTray = Guid.NewGuid();
    private static readonly Guid DressingSet = Guid.NewGuid();

    /// <summary>Two major trays and a dressing set for the operation theatre.</summary>
    private static CssdRequest Request() => new()
    {
        RequestNumber = "NHCQ26000001",
        Status = CssdRequestStatus.Open,
        Lines =
        [
            new CssdRequestLine { InstrumentSetId = MajorTray, Quantity = 2 },
            new CssdRequestLine { InstrumentSetId = DressingSet, Quantity = 1 },
        ],
    };

    private static int Issued(CssdRequest request, Guid setId) => request.Lines.Single(l => l.InstrumentSetId == setId).IssuedQuantity;

    [Fact]
    public void Issues_fill_the_lines_from_open_through_part_issued_to_issued()
    {
        var request = Request();
        request.IsOpen.ShouldBeTrue();

        request.RecordIssue(MajorTray);
        request.Status.ShouldBe(CssdRequestStatus.PartiallyIssued);
        request.IsOpen.ShouldBeTrue();

        request.RecordIssue(DressingSet);
        request.Status.ShouldBe(CssdRequestStatus.PartiallyIssued);

        request.RecordIssue(MajorTray);
        request.Status.ShouldBe(CssdRequestStatus.Issued);
        request.IsOpen.ShouldBeFalse();
        (Issued(request, MajorTray), Issued(request, DressingSet)).ShouldBe((2, 1));
    }

    [Fact]
    public void A_pack_of_a_set_not_asked_for_or_beyond_what_a_line_asks_for_is_not_counted()
    {
        var request = Request();

        request.RecordIssue(Guid.NewGuid());
        request.Status.ShouldBe(CssdRequestStatus.Open);

        request.RecordIssue(MajorTray);
        request.RecordIssue(MajorTray);
        request.RecordIssue(MajorTray);
        Issued(request, MajorTray).ShouldBe(2);
        request.Status.ShouldBe(CssdRequestStatus.PartiallyIssued, "the dressing set is still to come");
    }

    [Fact]
    public void An_open_or_part_issued_request_can_be_cancelled_with_the_reason()
    {
        var open = Request();
        open.Cancel("  Surgery postponed ").IsSuccess.ShouldBeTrue();
        open.Status.ShouldBe(CssdRequestStatus.Cancelled);
        open.CancelReason.ShouldBe("Surgery postponed");
        open.IsOpen.ShouldBeFalse();

        var partIssued = Request();
        partIssued.RecordIssue(MajorTray);
        partIssued.Cancel("The second tray is not needed").IsSuccess.ShouldBeTrue();
        partIssued.Status.ShouldBe(CssdRequestStatus.Cancelled);
        Issued(partIssued, MajorTray).ShouldBe(1, "what was issued stays issued");

        partIssued.RefreshStatus();
        partIssued.Status.ShouldBe(CssdRequestStatus.Cancelled);
    }

    [Fact]
    public void An_issued_or_cancelled_request_cannot_be_cancelled()
    {
        var issued = Request();
        issued.RecordIssue(MajorTray);
        issued.RecordIssue(MajorTray);
        issued.RecordIssue(DressingSet);
        issued.Cancel("Too late").Error!.Code.ShouldBe("cssd.request_closed");
        issued.Status.ShouldBe(CssdRequestStatus.Issued);
        issued.CancelReason.ShouldBeNull();

        var cancelled = Request();
        cancelled.Cancel("Surgery postponed").IsSuccess.ShouldBeTrue();
        cancelled.Cancel("Again").Error!.Code.ShouldBe("cssd.request_closed");
        cancelled.CancelReason.ShouldBe("Surgery postponed");
    }
}
