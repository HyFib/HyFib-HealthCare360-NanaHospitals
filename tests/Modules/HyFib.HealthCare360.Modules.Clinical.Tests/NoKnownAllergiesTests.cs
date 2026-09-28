using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Patient safety: the banner may say "No known allergies" only when a clinician recorded it, and never next to an
/// active allergy; an empty allergy list claims nothing.
/// </summary>
public sealed class NoKnownAllergiesTests
{
    [Theory]
    [InlineData(true, false, AllergyRecordingStatus.Recorded)]
    [InlineData(true, true, AllergyRecordingStatus.Recorded)]
    [InlineData(false, true, AllergyRecordingStatus.NoKnownAllergies)]
    [InlineData(false, false, AllergyRecordingStatus.NotRecorded)]
    public void Status_follows_active_allergies_first_then_the_statement(bool hasActiveAllergy, bool hasStatement, AllergyRecordingStatus expected) =>
        NoKnownAllergiesStatement.StatusOf(hasActiveAllergy, hasStatement).ShouldBe(expected);

    [Fact]
    public void A_stale_statement_never_hides_an_active_allergy() =>
        NoKnownAllergiesStatement.StatusOf(hasActiveAllergy: true, hasStatement: true).ShouldNotBe(AllergyRecordingStatus.NoKnownAllergies);

    [Fact]
    public void An_empty_list_without_a_statement_is_not_recorded_rather_than_no_known_allergies() =>
        NoKnownAllergiesStatement.StatusOf(hasActiveAllergy: false, hasStatement: false).ShouldBe(AllergyRecordingStatus.NotRecorded);

    [Fact]
    public void No_known_allergies_can_be_recorded_when_no_allergy_is_active() =>
        NoKnownAllergiesStatement.CanRecord(0).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(1, "1 active allergy.")]
    [InlineData(3, "3 active allergies.")]
    public void No_known_allergies_is_refused_while_allergies_are_active(int active, string mentions)
    {
        var result = NoKnownAllergiesStatement.CanRecord(active);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("allergy.active_exist");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        result.Error.Message.ShouldContain(mentions);
    }

    [Fact]
    public void Statuses_serialise_with_the_names_the_banner_expects() =>
        Enum.GetNames<AllergyRecordingStatus>().ShouldBe(["Recorded", "NoKnownAllergies", "NotRecorded"]);
}
