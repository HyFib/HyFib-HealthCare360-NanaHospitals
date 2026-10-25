using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>What a PvPI suspected adverse drug reaction report needs, when a serious one is due, and that a sent one stays.</summary>
public sealed class AdrReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);

    private static AdrDrug Suspect(string name = "Ceftriaxone 1 g Injection") =>
        new(name, "Lupin", "CT2407", new DateOnly(2027, 6, 30), "1 g", "IV", "Twice a day", Today.AddDays(-3), Today.AddDays(-1), "Pneumonia",
            Suspected: true, AdrActionTaken.Withdrawn, ChallengeResult.Yes, ChallengeResult.NotApplicable);

    private static AdrDetails Details(params AdrDrug[] drugs) =>
        new("Generalised urticaria and wheeze 20 minutes after the second dose", Today.AddDays(-1), null, AdrSeriousness.RequiredIntervention,
            AdrOutcome.Recovering, 62m, null, "Penicillin allergy not known", drugs);

    [Fact]
    public void A_report_names_a_suspected_drug_and_keeps_its_dates_in_order()
    {
        var report = new AdrReport { LearnedOn = Today };
        var concomitantOnly = Suspect("Paracetamol 500 mg") with { Suspected = false };
        report.Record(Details(concomitantOnly), Today).Error!.Fields!.Keys.ShouldBe(["drugs"]);
        report.Record(Details(Suspect() with { StoppedOn = Today.AddDays(-5) }), Today).Error!.Fields!.Keys.ShouldBe(["drugs"]);
        report.Record(Details(Suspect()) with { ReactionStartedOn = Today.AddDays(1) }, Today).Error!.Fields!.Keys.ShouldContain("reactionStartedOn");
        report.Record(Details(Suspect()), Today).IsSuccess.ShouldBeTrue();
        report.Drugs.ShouldHaveSingleItem().Name.ShouldBe("Ceftriaxone 1 g Injection");
    }

    [Fact]
    public void Death_as_the_seriousness_and_a_fatal_outcome_go_together()
    {
        var report = new AdrReport { LearnedOn = Today };
        report.Record(Details(Suspect()) with { Outcome = AdrOutcome.Fatal }, Today).Error!.Fields!.Keys.ShouldBe(["outcome"]);
        report.Record(Details(Suspect()) with { Outcome = AdrOutcome.Fatal, Seriousness = AdrSeriousness.Death }, Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_serious_reaction_is_due_within_15_days_of_the_hospital_learning_of_it()
    {
        var serious = new AdrReport { LearnedOn = Today.AddDays(-16) };
        serious.Record(Details(Suspect()) with { ReactionStartedOn = Today.AddDays(-17) }, Today).IsSuccess.ShouldBeTrue();
        serious.DueBy.ShouldBe(Today.AddDays(-1));
        serious.IsOverdue(Today).ShouldBeTrue();

        var mild = new AdrReport { LearnedOn = Today.AddDays(-40) };
        mild.Record(Details(Suspect()) with { Seriousness = AdrSeriousness.None, ReactionStartedOn = Today.AddDays(-41) }, Today).IsSuccess.ShouldBeTrue();
        (mild.DueBy, mild.IsOverdue(Today)).ShouldBe((null, false));
    }

    [Fact]
    public void Once_sent_with_its_causality_the_report_does_not_change()
    {
        var report = new AdrReport { LearnedOn = Today.AddDays(-1) };
        report.Record(Details(Suspect()), Today).IsSuccess.ShouldBeTrue();
        report.Submit(WhoUmcCausality.Probable, "Dr. Meera (pharmacology)", Today.AddDays(1), null, Today).Error!.Fields!.Keys.ShouldBe(["submittedOn"]);
        report.Submit(WhoUmcCausality.Probable, "Dr. Meera (pharmacology)", Today, " AMC-TN-2026-1187 ", Today).IsSuccess.ShouldBeTrue();
        (report.Status, report.Causality, report.SubmissionReference).ShouldBe((AdrReportStatus.Submitted, WhoUmcCausality.Probable, "AMC-TN-2026-1187"));
        report.Record(Details(Suspect()), Today).Error!.Code.ShouldBe("adr.submitted");
        report.IsOverdue(Today.AddDays(30)).ShouldBeFalse();
    }
}
