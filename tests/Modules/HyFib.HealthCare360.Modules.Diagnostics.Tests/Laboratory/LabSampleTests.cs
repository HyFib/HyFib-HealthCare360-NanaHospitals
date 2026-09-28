using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>Sample state machine: Pending → Collected → Received → InProcess → Completed, with rejection and recollection.</summary>
public sealed class LabSampleTests
{
    [Fact]
    public void Collection_records_who_when_and_where()
    {
        var sample = Sample();

        sample.Collect(UserId, Now, "OP collection counter").IsSuccess.ShouldBeTrue();

        sample.Status.ShouldBe(SampleStatus.Collected);
        sample.CollectedBy.ShouldBe(UserId);
        sample.CollectedAt.ShouldBe(Now);
        sample.CollectionSite.ShouldBe("OP collection counter");
    }

    [Fact]
    public void A_sample_is_collected_only_once()
    {
        var sample = Sample();
        sample.Collect(UserId, Now, null);

        sample.Collect(UserId, Now, null).Error!.Code.ShouldBe("sample.not_pending");
    }

    [Fact]
    public void Receipt_requires_collection_so_payment_gating_cannot_be_bypassed() =>
        Sample().Receive(UserId, Now).Error!.Code.ShouldBe("sample.not_collected");

    [Fact]
    public void A_second_scan_at_receipt_is_reported_as_a_conflict()
    {
        var sample = Sample();
        sample.Collect(UserId, Now, null);
        sample.Receive(UserId, Now.AddMinutes(20));

        var again = sample.Receive(UserId, Now.AddMinutes(25));

        again.Error!.Code.ShouldBe("sample.already_received");
        again.Error.Type.ShouldBe(ErrorType.Conflict);
        sample.ReceivedAt.ShouldBe(Now.AddMinutes(20));
    }

    [Fact]
    public void Receipt_cannot_be_earlier_than_collection()
    {
        var sample = Sample();
        sample.Collect(UserId, Now, null);

        sample.Receive(UserId, Now.AddMinutes(-1)).Error!.Code.ShouldBe("sample.receipt_before_collection");
    }

    [Fact]
    public void Processing_starts_after_receipt_and_is_idempotent()
    {
        var sample = Sample();
        sample.Collect(UserId, Now, null);
        sample.StartProcessing().Error!.Code.ShouldBe("sample.not_received");

        sample.Receive(UserId, Now.AddMinutes(10));
        sample.StartProcessing().IsSuccess.ShouldBeTrue();
        sample.StartProcessing().IsSuccess.ShouldBeTrue();
        sample.Status.ShouldBe(SampleStatus.InProcess);
    }

    [Fact]
    public void Completion_happens_in_the_lab_and_a_reopened_result_brings_the_tube_back()
    {
        var sample = Sample(SampleStatus.Collected);
        sample.Complete(Now).Error!.Code.ShouldBe("sample.not_in_lab");

        sample.Status = SampleStatus.InProcess;
        sample.Complete(Now).IsSuccess.ShouldBeTrue();
        sample.CompletedAt.ShouldBe(Now);

        sample.Reopen();
        sample.Status.ShouldBe(SampleStatus.InProcess);
        sample.CompletedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(SampleStatus.Collected)]
    [InlineData(SampleStatus.Received)]
    [InlineData(SampleStatus.InProcess)]
    public void Collected_and_in_lab_samples_can_be_rejected(SampleStatus status)
    {
        var sample = Sample(status);

        sample.Reject(SampleRejectionReason.Haemolysed, "Grossly haemolysed", UserId, Now).IsSuccess.ShouldBeTrue();

        sample.Status.ShouldBe(SampleStatus.Rejected);
        sample.RejectionReason.ShouldBe(SampleRejectionReason.Haemolysed);
        sample.RejectedBy.ShouldBe(UserId);
    }

    [Theory]
    [InlineData(SampleStatus.Pending)]
    [InlineData(SampleStatus.Completed)]
    [InlineData(SampleStatus.Rejected)]
    public void Samples_not_yet_drawn_or_already_finished_cannot_be_rejected(SampleStatus status) =>
        Sample(status).Reject(SampleRejectionReason.Clotted, null, UserId, Now).Error!.Code.ShouldBe("sample.not_rejectable");

    [Fact]
    public void Rejection_for_another_reason_needs_remarks() =>
        Sample(SampleStatus.Received).Reject(SampleRejectionReason.Other, " ", UserId, Now).Error!.Fields!.Keys.ShouldContain("remarks");

    [Fact]
    public void Cancelling_is_refused_after_completion()
    {
        Sample().Cancel().IsSuccess.ShouldBeTrue();
        Sample(SampleStatus.Completed).Cancel().Error!.Code.ShouldBe("sample.closed");
    }

    [Fact]
    public void A_rejected_sample_gets_one_linked_recollection_tube()
    {
        var sample = Sample(SampleStatus.Received, LabSection.Hematology);
        sample.DedicatedTestId = Guid.CreateVersion7();
        sample.Reject(SampleRejectionReason.Clotted, null, UserId, Now);

        var recollection = sample.CreateRecollection("000000456").Value;

        recollection.Status.ShouldBe(SampleStatus.Pending);
        recollection.Barcode.ShouldBe("000000456");
        recollection.Section.ShouldBe(LabSection.Hematology);
        recollection.Container.ShouldBe(sample.Container);
        recollection.DedicatedTestId.ShouldBe(sample.DedicatedTestId);
        recollection.RecollectionOfSampleId.ShouldBe(sample.Id);
        sample.RecollectedAsSampleId.ShouldBe(recollection.Id);
        sample.CreateRecollection("000000789").Error!.Code.ShouldBe("sample.recollection_exists");
    }

    [Fact]
    public void Only_rejected_samples_are_recollected() =>
        Sample(SampleStatus.Received).CreateRecollection("000000456").Error!.Code.ShouldBe("sample.not_rejected");
}
