using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Who acknowledges a critical lab result where the lab asks for a second pathologist: the doctor who ordered it, or a
/// doctor who records that they cover the patient and why; nobody else.
/// </summary>
public sealed class CriticalAcknowledgementTests
{
    private static readonly Guid OrderingDoctor = Guid.CreateVersion7();
    private static readonly Guid OtherDoctor = Guid.CreateVersion7();

    private static ResultReview Critical() => new() { Title = "Electrolytes", HasCriticalFindings = true, PractitionerId = OrderingDoctor };

    [Fact]
    public void The_ordering_doctor_acknowledges_without_a_reason() =>
        Critical().CanAcknowledgeCritical(OrderingDoctor, null).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Another_doctor_acknowledges_only_recording_that_they_cover_the_patient_and_why()
    {
        var review = Critical();
        review.CanAcknowledgeCritical(OtherDoctor, null).Error!.Code.ShouldBe("validation");
        review.CanAcknowledgeCritical(OtherDoctor, "on call").Error!.Code.ShouldBe("validation", "a reason of a word or two says nothing");
        review.CanAcknowledgeCritical(OtherDoctor, "Night duty doctor covering ward 4").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Someone_who_is_not_a_doctor_does_not_acknowledge_even_with_a_reason() =>
        Critical().CanAcknowledgeCritical(null, "Nurse in charge of ward 4 tonight").Error!.Code.ShouldBe("result.critical_doctor_only");

    [Fact]
    public void A_result_with_no_ordering_doctor_is_acknowledged_by_a_doctor_who_covers_the_patient()
    {
        var walkIn = new ResultReview { Title = "Electrolytes", HasCriticalFindings = true };
        walkIn.CanAcknowledgeCritical(OtherDoctor, null).IsFailure.ShouldBeTrue();
        walkIn.CanAcknowledgeCritical(OtherDoctor, "Casualty medical officer on duty").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_covering_reason_is_kept_with_the_acknowledgement_and_cleared_when_the_report_is_released_again()
    {
        var review = Critical();
        review.Acknowledge(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow, "Night duty doctor covering ward 4").IsSuccess.ShouldBeTrue();
        review.CoveringReason.ShouldBe("Night duty doctor covering ward 4");
        review.Reopen("Electrolytes (amended)", hasCriticalFindings: true, DateTimeOffset.UtcNow);
        review.CoveringReason.ShouldBeNull();
    }
}
