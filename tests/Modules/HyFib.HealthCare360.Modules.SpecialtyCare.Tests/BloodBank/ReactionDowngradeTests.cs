using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>
/// A transfusion reaction lowered (maker-checker, slice 15): a lower severity, or a weaker imputability than one already
/// assessed, is a lowering that waits for the medical officer; raising either, or a first assessment, is not.
/// </summary>
public sealed class ReactionDowngradeTests
{
    [Fact]
    public void A_lower_severity_or_a_weaker_imputability_is_a_lowering_in_words()
    {
        ReactionDowngrade.Lowers(ReactionSeverity.Severe, ReactionImputability.Probable, ReactionSeverity.Mild, ReactionImputability.Unlikely)
            .ShouldBe(["severity Severe → Mild", "imputability Probable → Unlikely"]);
        ReactionDowngrade.Lowers(ReactionSeverity.Moderate, ReactionImputability.Definite, ReactionSeverity.Moderate, ReactionImputability.Possible)
            .ShouldBe(["imputability Definite → Possible"]);
    }

    [Fact]
    public void Raising_either_or_a_first_assessment_lowers_nothing()
    {
        ReactionDowngrade.Lowers(ReactionSeverity.Mild, ReactionImputability.Possible, ReactionSeverity.Severe, ReactionImputability.Definite).ShouldBeEmpty();
        ReactionDowngrade.Lowers(ReactionSeverity.Moderate, ReactionImputability.NotAssessed, ReactionSeverity.Moderate, ReactionImputability.Excluded).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ReactionImputability.NotAssessed, true)]
    [InlineData(ReactionImputability.Excluded, false)]
    [InlineData(ReactionImputability.Unlikely, false)]
    [InlineData(ReactionImputability.Possible, true)]
    [InlineData(ReactionImputability.Definite, true)]
    public void A_reaction_is_reported_to_haemovigilance_until_it_is_unlikely_or_excluded(ReactionImputability imputability, bool reportable) =>
        ReactionDowngrade.Reportable(imputability).ShouldBe(reportable);

    [Fact]
    public void A_classification_moves_a_reported_reaction_under_investigation_and_a_closed_one_is_not_changed()
    {
        var reaction = new TransfusionReaction { Severity = ReactionSeverity.Moderate, Status = ReactionStatus.Reported };
        reaction.Classify(ReactionType.FebrileNonHaemolytic, ReactionSeverity.Severe, ReactionImputability.Probable).IsSuccess.ShouldBeTrue();
        (reaction.Status, reaction.Severity, reaction.Imputability).ShouldBe((ReactionStatus.UnderInvestigation, ReactionSeverity.Severe, ReactionImputability.Probable));

        reaction.Close(ReactionType.FebrileNonHaemolytic, ReactionSeverity.Severe, ReactionImputability.NotAssessed, "Findings", true, "Officer", DateTimeOffset.UtcNow)
            .Error!.Code.ShouldBe("validation");
        reaction.Close(ReactionType.FebrileNonHaemolytic, ReactionSeverity.Severe, ReactionImputability.Probable, "Findings", true, "Officer", DateTimeOffset.UtcNow)
            .IsSuccess.ShouldBeTrue();
        reaction.Classify(ReactionType.FebrileNonHaemolytic, ReactionSeverity.Mild, ReactionImputability.Unlikely).Error!.Code.ShouldBe("bloodbank.reaction_closed");
    }
}
