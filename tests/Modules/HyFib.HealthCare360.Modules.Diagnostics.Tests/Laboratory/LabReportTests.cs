using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>Accession, test and report status rules: release per test, partial reports, QR code and amendments.</summary>
public sealed class LabReportTests
{
    [Fact]
    public void Release_publishes_each_verified_test_to_the_EHR_and_leaves_the_rest_pending()
    {
        var cbc = Item(LabOrderItemStatus.Verified, "CBC");
        var culture = Item(LabOrderItemStatus.InLab, "Urine culture");
        var (order, report) = Accession(cbc, culture);

        var released = report.Release(order, new HashSet<Guid> { cbc.Id }, UserId, Now).Value;

        released.ShouldHaveSingleItem().ShouldBe(cbc);
        cbc.Status.ShouldBe(LabOrderItemStatus.Released);
        cbc.ReleasedBy.ShouldBe(UserId);
        culture.Status.ShouldBe(LabOrderItemStatus.InLab);
        report.Status.ShouldBe(LabReportStatus.Released);
        report.IsPartial.ShouldBeTrue();
        report.FirstReleasedAt.ShouldBe(Now);
        order.Status.ShouldBe(LabOrderStatus.InProgress);

        var announced = report.DomainEvents.OfType<DiagnosticReportReleased>().ShouldHaveSingleItem();
        announced.Kind.ShouldBe(DiagnosticKind.Laboratory);
        announced.ReportId.ShouldBe(report.Id);
        announced.PatientId.ShouldBe(order.PatientId);
        announced.VisitId.ShouldBe(order.VisitId);
        announced.OrderItemId.ShouldBe(cbc.SourceOrderItemId);
        announced.Title.ShouldBe("CBC");
        announced.HasCriticalFindings.ShouldBeTrue();
    }

    [Fact]
    public void The_first_release_assigns_the_QR_verification_code_and_later_releases_keep_it()
    {
        var first = Item(LabOrderItemStatus.Verified, "CBC");
        var second = Item(LabOrderItemStatus.ResultsEntered, "LFT");
        var (order, report) = Accession(first, second);

        report.Release(order, new HashSet<Guid>(), UserId, Now);
        var code = report.VerificationCode;
        second.Status = LabOrderItemStatus.Verified;
        report.Release(order, new HashSet<Guid>(), UserId, Now.AddHours(3)).IsSuccess.ShouldBeTrue();

        VerificationCode.IsWellFormed(code).ShouldBeTrue();
        report.VerificationCode.ShouldBe(code);
        report.FirstReleasedAt.ShouldBe(Now);
        report.ReleasedAt.ShouldBe(Now.AddHours(3));
        report.IsPartial.ShouldBeFalse();
        order.Status.ShouldBe(LabOrderStatus.Completed);
        order.CompletedAt.ShouldBe(Now.AddHours(3));
        report.DomainEvents.OfType<DiagnosticReportReleased>().Count().ShouldBe(2);
    }

    [Fact]
    public void Nothing_verified_means_nothing_to_release()
    {
        var (order, report) = Accession(Item(LabOrderItemStatus.ResultsEntered), Item(LabOrderItemStatus.Released));

        report.Release(order, new HashSet<Guid>(), UserId, Now).Error!.Code.ShouldBe("lab.nothing_to_release");
        report.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Report_status_follows_the_tests_and_ignores_cancelled_ones()
    {
        var report = new LabReport();

        report.Refresh([Item(LabOrderItemStatus.Verified), Item(LabOrderItemStatus.Cancelled)]);
        report.Status.ShouldBe(LabReportStatus.Verified);

        report.Refresh([Item(LabOrderItemStatus.Verified), Item(LabOrderItemStatus.InLab)]);
        report.Status.ShouldBe(LabReportStatus.Draft);

        report.Refresh([Item(LabOrderItemStatus.Released), Item(LabOrderItemStatus.Cancelled)]);
        report.Status.ShouldBe(LabReportStatus.Released);
        report.IsPartial.ShouldBeFalse();

        report.Refresh([Item(LabOrderItemStatus.Cancelled)]);
        report.Status.ShouldBe(LabReportStatus.Draft);
    }

    [Fact]
    public void Only_a_released_report_is_amended()
    {
        var (order, report) = Accession(Item(LabOrderItemStatus.Verified));

        report.Amend(order, "Transcription error", "K: 6.8 -> 4.8 mmol/L", [], false, UserId, Now).Error!.Code
            .ShouldBe("lab.report_not_released");
        report.Revision.ShouldBe(0);
    }

    [Fact]
    public void An_amendment_is_numbered_recorded_and_re_announced_to_the_EHR()
    {
        var electrolytes = Item(LabOrderItemStatus.Verified, "Serum electrolytes");
        var (order, report) = Accession(electrolytes);
        report.Release(order, new HashSet<Guid>(), UserId, Now);
        report.ClearDomainEvents();

        report.Amend(order, "Haemolysed sample, repeated on a fresh draw", "K: 6.8 -> 4.8 mmol/L", [electrolytes], false, UserId, Now.AddHours(1))
            .IsSuccess.ShouldBeTrue();

        report.Revision.ShouldBe(1);
        report.Status.ShouldBe(LabReportStatus.Amended);
        var amendment = report.Amendments.ShouldHaveSingleItem();
        amendment.Revision.ShouldBe(1);
        amendment.Reason.ShouldBe("Haemolysed sample, repeated on a fresh draw");
        amendment.Changes.ShouldBe("K: 6.8 -> 4.8 mmol/L");
        amendment.AmendedBy.ShouldBe(UserId);
        report.DomainEvents.OfType<DiagnosticReportReleased>().ShouldHaveSingleItem().Title.ShouldBe("Serum electrolytes (amended)");

        report.Amend(order, "Second correction", new string('x', 2500), [electrolytes], true, UserId, Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        report.Revision.ShouldBe(2);
        report.Amendments[^1].Changes.Length.ShouldBe(2000);
    }

    [Fact]
    public void Adding_a_STAT_test_makes_the_whole_accession_STAT()
    {
        var order = new LabOrder { FacilityId = Guid.CreateVersion7() };
        var routine = Item(LabOrderItemStatus.Ordered);
        var stat = Item(LabOrderItemStatus.Ordered);
        stat.Priority = OrderPriority.Stat;

        order.AddItem(routine);
        order.AddItem(stat);
        order.AddItem(Item(LabOrderItemStatus.Ordered));

        order.Priority.ShouldBe(OrderPriority.Stat);
        stat.LabOrderId.ShouldBe(order.Id);
        stat.FacilityId.ShouldBe(order.FacilityId);
    }

    [Fact]
    public void Accession_status_is_derived_from_its_tests()
    {
        var first = Item(LabOrderItemStatus.Ordered);
        var second = Item(LabOrderItemStatus.Ordered);
        var (order, _) = Accession(first, second);

        order.RefreshStatus(Now);
        order.Status.ShouldBe(LabOrderStatus.Ordered);
        order.AcceptsNewItems.ShouldBeTrue();

        first.Status = LabOrderItemStatus.Collected;
        order.RefreshStatus(Now);
        order.Status.ShouldBe(LabOrderStatus.InProgress);

        first.Cancel("Duplicate order", Now);
        second.Cancel("Patient left", Now);
        order.RefreshStatus(Now);
        order.Status.ShouldBe(LabOrderStatus.Cancelled);
        order.AcceptsNewItems.ShouldBeFalse();
    }

    [Theory]
    [InlineData(LabOrderItemStatus.Verified)]
    [InlineData(LabOrderItemStatus.Released)]
    public void Verified_results_never_disappear_through_cancellation(LabOrderItemStatus status) =>
        Item(status).Cancel("Not required", Now).Error!.Code.ShouldBe("lab.item_completed");

    [Fact]
    public void Cancellation_records_the_reason_once()
    {
        var item = Item(LabOrderItemStatus.InLab);

        item.Cancel("Clinician withdrew the order", Now).IsSuccess.ShouldBeTrue();
        item.Cancel("Again", Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        item.IsActive.ShouldBeFalse();
        item.CancelReason.ShouldBe("Clinician withdrew the order");
        item.CancelledAt.ShouldBe(Now);
    }

    [Fact]
    public void Test_status_follows_its_results()
    {
        var item = Item(LabOrderItemStatus.InLab);
        var first = Result(LabResultStatus.Entered);
        var second = Result(LabResultStatus.Pending);

        item.RefreshFromResults([first, second], Now);
        item.Status.ShouldBe(LabOrderItemStatus.InLab);

        second.Status = LabResultStatus.Entered;
        item.RefreshFromResults([first, second], Now);
        item.Status.ShouldBe(LabOrderItemStatus.ResultsEntered);

        first.Status = LabResultStatus.Verified;
        second.Status = LabResultStatus.Verified;
        item.RefreshFromResults([first, second], Now);
        item.Status.ShouldBe(LabOrderItemStatus.Verified);
        item.VerifiedAt.ShouldBe(Now);

        second.Status = LabResultStatus.Entered;
        item.RefreshFromResults([first, second], Now.AddMinutes(5));
        item.Status.ShouldBe(LabOrderItemStatus.ResultsEntered);
        item.VerifiedAt.ShouldBeNull();
    }

    [Fact]
    public void Released_and_cancelled_tests_are_not_moved_by_result_changes()
    {
        var released = Item(LabOrderItemStatus.Released);
        var cancelled = Item(LabOrderItemStatus.Cancelled);

        released.RefreshFromResults([Result(LabResultStatus.Entered)], Now);
        cancelled.RefreshFromResults([Result(LabResultStatus.Verified)], Now);

        released.Status.ShouldBe(LabOrderItemStatus.Released);
        cancelled.Status.ShouldBe(LabOrderItemStatus.Cancelled);
    }

    [Fact]
    public void Only_a_verified_test_is_released() =>
        Item(LabOrderItemStatus.ResultsEntered).MarkReleased(UserId, Now).Error!.Code.ShouldBe("lab.item_not_verified");

    [Fact]
    public void A_rejected_tube_puts_its_tests_back_in_the_collection_queue()
    {
        var waiting = Item(LabOrderItemStatus.InLab);
        var verified = Item(LabOrderItemStatus.Verified);
        var newTube = Guid.CreateVersion7();

        waiting.AwaitRecollection(newTube);
        verified.AwaitRecollection(newTube);

        waiting.Status.ShouldBe(LabOrderItemStatus.Ordered);
        waiting.SampleId.ShouldBe(newTube);
        verified.Status.ShouldBe(LabOrderItemStatus.Verified);
    }

    [Fact]
    public void Outsourcing_tracks_dispatch_and_the_external_report()
    {
        var item = Item(LabOrderItemStatus.Ordered, "Vitamin D");
        var referralLab = Guid.CreateVersion7();
        item.SendOut(referralLab, "DKT-1", UserId, Now).Error!.Code.ShouldBe("lab.outsource_state");
        item.ReceiveExternalReport("EXT-9", null, Now).Error!.Code.ShouldBe("lab.outsource_not_sent");

        item.Status = LabOrderItemStatus.Collected;
        item.SendOut(referralLab, " DKT-1 ", UserId, Now).IsSuccess.ShouldBeTrue();
        item.SendOut(referralLab, "DKT-2", UserId, Now).Error!.Code.ShouldBe("lab.outsource_sent");

        var file = Guid.CreateVersion7();
        item.ReceiveExternalReport(" EXT-9 ", file, Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        item.ReferralLabId.ShouldBe(referralLab);
        item.DispatchReference.ShouldBe("DKT-1");
        item.ExternalReference.ShouldBe("EXT-9");
        item.ExternalReportFileId.ShouldBe(file);
        item.ExternalReportReceivedAt.ShouldBe(Now.AddDays(1));
    }

    private static (LabOrder Order, LabReport Report) Accession(params LabOrderItem[] items)
    {
        var order = new LabOrder
        {
            FacilityId = Guid.CreateVersion7(),
            PatientId = Guid.CreateVersion7(),
            VisitId = Guid.CreateVersion7(),
            AccessionNumber = "NHLB260000123",
            PatientClass = PatientClass.OutPatient,
            OrderedAt = Now.AddHours(-4),
        };
        foreach (var item in items)
        {
            order.AddItem(item);
        }

        var report = new LabReport
        {
            FacilityId = order.FacilityId,
            LabOrderId = order.Id,
            PatientId = order.PatientId,
            ReportNumber = order.AccessionNumber,
        };
        return (order, report);
    }
}
