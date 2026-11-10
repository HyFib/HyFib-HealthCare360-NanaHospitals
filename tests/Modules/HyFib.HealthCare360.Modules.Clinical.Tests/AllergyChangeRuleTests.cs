using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Which allergy changes need a second clinician (maker-checker, slice 7): what takes an allergy out of the prescribing
/// checks or softens its alert waits; what makes it stricter does not.
/// </summary>
public sealed class AllergyChangeRuleTests
{
    private static readonly AllergyStanding SevereActive = new(AllergyStatus.Active, AllergySeverity.Severe);

    [Theory]
    [InlineData(AllergyStatus.Inactive)]
    [InlineData(AllergyStatus.Resolved)]
    [InlineData(AllergyStatus.EnteredInError)]
    public void Taking_an_active_allergy_out_of_the_checks_waits(AllergyStatus status) =>
        AllergyChangeRule.Waits(SevereActive, new AllergyStanding(status, AllergySeverity.Severe)).ShouldBe(new AllergyStanding(status, AllergySeverity.Severe));

    [Fact]
    public void A_lower_severity_waits_and_a_higher_one_is_made_at_once()
    {
        AllergyChangeRule.Waits(SevereActive, SevereActive with { Severity = AllergySeverity.Mild }).ShouldBe(SevereActive with { Severity = AllergySeverity.Mild });
        AllergyChangeRule.Waits(SevereActive, SevereActive with { Severity = AllergySeverity.Anaphylaxis }).ShouldBeNull();
        AllergyChangeRule.Waits(SevereActive, SevereActive).ShouldBeNull();
    }

    [Fact]
    public void Deactivating_with_a_higher_severity_asks_only_for_the_deactivation_and_keeps_the_present_severity()
    {
        AllergyChangeRule.Waits(SevereActive, new AllergyStanding(AllergyStatus.Inactive, AllergySeverity.Anaphylaxis))
            .ShouldBe(new AllergyStanding(AllergyStatus.Inactive, AllergySeverity.Severe));
    }

    [Fact]
    public void An_allergy_in_no_check_changes_at_once()
    {
        var inactive = new AllergyStanding(AllergyStatus.Inactive, AllergySeverity.Severe);
        AllergyChangeRule.Waits(inactive, inactive with { Severity = AllergySeverity.Mild }).ShouldBeNull();
        AllergyChangeRule.Waits(inactive, new AllergyStanding(AllergyStatus.Active, AllergySeverity.Mild)).ShouldBeNull("made active again is stricter");
    }

    [Fact]
    public void The_change_is_put_in_words() =>
        AllergyChangeRule.Describe(SevereActive, new AllergyStanding(AllergyStatus.EnteredInError, AllergySeverity.Mild))
            .ShouldBe("active → entered in error; severity severe → mild");
}
