using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class DischargeFollowUpPolicyTests
{
    [Theory]
    [InlineData("Normal", true, true, FollowUpPriority.Normal)]
    [InlineData("Referred", true, true, FollowUpPriority.Normal)]
    [InlineData("Lama", true, true, FollowUpPriority.High)]
    [InlineData("DAMA", true, true, FollowUpPriority.High)]
    [InlineData("Absconded", true, false, FollowUpPriority.High)]
    [InlineData("Expired", false, false, FollowUpPriority.Normal)]
    [InlineData(" expired ", false, false, FollowUpPriority.Normal)]
    public void Discharge_type_decides_the_call_the_survey_and_the_priority(string type, bool call, bool survey, FollowUpPriority priority)
    {
        DischargeFollowUpPolicy.ShouldCall(type).ShouldBe(call);
        DischargeFollowUpPolicy.ShouldRequestFeedback(type).ShouldBe(survey);
        DischargeFollowUpPolicy.PriorityFor(type).ShouldBe(priority);
    }

    [Fact]
    public void The_call_is_due_two_days_after_the_local_discharge_date() =>
        DischargeFollowUpPolicy.CallDate(new DateOnly(2026, 9, 30)).ShouldBe(new DateOnly(2026, 10, 2));

    [Fact]
    public void The_idempotency_key_is_per_admission()
    {
        var admission = Guid.NewGuid();

        DischargeFollowUpPolicy.SourceReference(admission).ShouldBe(DischargeFollowUpPolicy.SourceReference(admission));
        DischargeFollowUpPolicy.SourceReference(admission).ShouldNotBe(DischargeFollowUpPolicy.SourceReference(Guid.NewGuid()));
        DischargeFollowUpPolicy.SourceReference(admission).Length.ShouldBeLessThanOrEqualTo(100);
    }
}
