using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Dialysis;

/// <summary>
/// A dialysis patient's infection status (maker-checker, slice 15): a sero-negative patient found positive is isolated
/// at once; taking a patient off a positive status waits for a second person; the lab result must test for the infection
/// concerned and be recent.
/// </summary>
public sealed class InfectionStatusChangeTests
{
    [Theory]
    [InlineData(SeroStatus.Negative, SeroStatus.HBsAg, false)]
    [InlineData(SeroStatus.Negative, SeroStatus.HIV, false)]
    [InlineData(SeroStatus.HBsAg, SeroStatus.Negative, true)]
    [InlineData(SeroStatus.HCV, SeroStatus.HIV, true)]
    public void Only_taking_a_patient_off_a_positive_status_waits(SeroStatus current, SeroStatus asked, bool waits) =>
        InfectionStatusChange.Waits(current, asked).ShouldBe(waits);

    [Fact]
    public void The_result_tests_for_the_infection_asked_for_or_back_to_negative_the_one_the_patient_has()
    {
        InfectionStatusChange.Tested(SeroStatus.Negative, SeroStatus.HCV).ShouldBe(SeroStatus.HCV);
        InfectionStatusChange.Tested(SeroStatus.HBsAg, SeroStatus.Negative).ShouldBe(SeroStatus.HBsAg);
    }

    [Theory]
    [InlineData(SeroStatus.HBsAg, "LAB-HBSAG", "Hepatitis B surface antigen (HBsAg)", true)]
    [InlineData(SeroStatus.HCV, "LAB-HCV", "Anti-HCV antibodies", true)]
    [InlineData(SeroStatus.HIV, "LAB-HIV", "HIV 1 & 2 antibodies with p24 antigen", true)]
    [InlineData(SeroStatus.HBsAg, "LAB-HCV", "Anti-HCV antibodies", false)]
    [InlineData(SeroStatus.HIV, "LAB-FBS", "Fasting blood sugar", false)]
    public void A_result_supports_only_the_infection_it_tests_for(SeroStatus infection, string code, string name, bool supports) =>
        InfectionStatusChange.TestsFor(infection, code, name).ShouldBe(supports);

    [Fact]
    public void A_result_older_than_the_rescreening_interval_or_in_the_future_does_not_decide_the_status()
    {
        var today = new DateOnly(2026, 11, 8);
        InfectionStatusChange.Recent(today.AddDays(-90), today).ShouldBeTrue();
        InfectionStatusChange.Recent(today.AddDays(-91), today).ShouldBeFalse();
        InfectionStatusChange.Recent(today.AddDays(1), today).ShouldBeFalse();
    }
}
