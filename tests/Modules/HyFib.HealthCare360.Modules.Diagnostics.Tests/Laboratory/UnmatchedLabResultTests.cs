using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>An analyser result kept aside is resolved once: assigned to a test, or discarded with the reason.</summary>
public sealed class UnmatchedLabResultTests
{
    private static UnmatchedLabResult Kept() => new()
    {
        FacilityId = Guid.CreateVersion7(),
        InstrumentCode = "EM360",
        SampleBarcode = "000000999",
        ChannelCode = "GLU",
        Value = "98",
        ReceivedAt = Now,
        Reason = UnmatchedResultReason.UnknownSample,
        Detail = "No sample of EM360's facility has the barcode 000000999.",
    };

    [Fact]
    public void Assigning_records_the_test_who_and_when()
    {
        var kept = Kept();
        var resultId = Guid.CreateVersion7();

        kept.Assign(resultId, UserId, Now).IsSuccess.ShouldBeTrue();

        (kept.Status, kept.AssignedResultId, kept.ResolvedBy, kept.ResolvedAt).ShouldBe((UnmatchedResultStatus.Assigned, resultId, UserId, Now));
    }

    [Fact]
    public void Discarding_records_the_reason()
    {
        var kept = Kept();

        kept.Discard("  QC material run without a sample id ", UserId, Now).IsSuccess.ShouldBeTrue();

        (kept.Status, kept.DiscardReason, kept.ResolvedBy).ShouldBe((UnmatchedResultStatus.Discarded, "QC material run without a sample id", UserId));
    }

    [Fact]
    public void A_result_is_resolved_once()
    {
        var assigned = Kept();
        assigned.Assign(Guid.CreateVersion7(), UserId, Now);
        var discarded = Kept();
        discarded.Discard("Duplicate of the rerun", UserId, Now);

        assigned.Discard("Changed my mind", UserId, Now).Error!.Code.ShouldBe("lab.unmatched_resolved");
        assigned.Assign(Guid.CreateVersion7(), UserId, Now).Error!.Message.ShouldBe("The GLU result from EM360 was already assigned.");
        discarded.Assign(Guid.CreateVersion7(), UserId, Now).Error!.Message.ShouldBe("The GLU result from EM360 was already discarded.");
        assigned.Status.ShouldBe(UnmatchedResultStatus.Assigned);
        discarded.Status.ShouldBe(UnmatchedResultStatus.Discarded);
    }

    [Fact]
    public void The_analysers_withdrawal_of_a_verified_result_is_never_put_on_a_test_only_discarded()
    {
        var withdrawal = Kept();
        withdrawal.Reason = UnmatchedResultReason.Withdrawn;

        withdrawal.EnsureAssignable().Error!.Code.ShouldBe("lab.unmatched_withdrawn");
        withdrawal.Assign(Guid.CreateVersion7(), UserId, Now).Error!.Message
            .ShouldBe("EM360 withdrew its GLU result, so it cannot go on a test. Check the verified result it names, then discard it.");
        withdrawal.Status.ShouldBe(UnmatchedResultStatus.Open);
        withdrawal.Discard("Checked: the verified result is right.", UserId, Now).IsSuccess.ShouldBeTrue();
        Kept().EnsureAssignable().IsSuccess.ShouldBeTrue();
    }
}
