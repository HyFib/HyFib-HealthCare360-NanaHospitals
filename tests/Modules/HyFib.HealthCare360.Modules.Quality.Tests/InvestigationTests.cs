using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class InvestigationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Investigator = Guid.NewGuid();

    private static Investigation FiveWhys(int whys, bool rootCause = true) => new()
    {
        Method = RcaMethod.FiveWhys,
        ProblemStatement = "Patient fell while walking to the washroom at night",
        Findings = "Call bell out of reach; bed rails down; floor wet",
        Whys = Enumerable.Range(1, whys).Select(i => $"Because {i}").ToList(),
        Factors = rootCause ? [new RcaFactor { Category = ContributingFactorCategory.Process, Description = "No fall-risk reassessment at night", IsRootCause = true }] : [],
    };

    [Fact]
    public void A_complete_five_whys_analysis_can_be_closed()
    {
        var rca = FiveWhys(3);

        rca.Complete(Investigator, Now).IsSuccess.ShouldBeTrue();

        rca.Status.ShouldBe(InvestigationStatus.Completed);
        rca.CompletedAt.ShouldBe(Now);
        rca.CompletedByUserId.ShouldBe(Investigator);
    }

    [Fact]
    public void Five_whys_needs_at_least_three_whys()
    {
        FiveWhys(2).Complete(Investigator, Now).Error!.Code.ShouldBe("rca.whys");
    }

    [Fact]
    public void An_analysis_without_a_root_cause_cannot_be_completed()
    {
        FiveWhys(5, rootCause: false).Complete(Investigator, Now).Error!.Code.ShouldBe("rca.no_root_cause");
    }

    [Fact]
    public void Problem_statement_and_findings_are_required()
    {
        var rca = FiveWhys(3);
        rca.Findings = " ";

        rca.Complete(Investigator, Now).Error!.Code.ShouldBe("rca.incomplete");
    }

    [Fact]
    public void Fishbone_needs_at_least_two_factors()
    {
        var rca = new Investigation
        {
            Method = RcaMethod.Fishbone,
            ProblemStatement = "Wrong drug administered",
            Findings = "Look-alike ampoules stored together",
            Factors = [new RcaFactor { Category = ContributingFactorCategory.Equipment, Description = "Look-alike packaging", IsRootCause = true }],
        };

        rca.Complete(Investigator, Now).Error!.Code.ShouldBe("rca.factors");

        rca.Factors.Add(new RcaFactor { Category = ContributingFactorCategory.Communication, Description = "Verbal order not read back" });
        rca.Complete(Investigator, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_completed_analysis_can_be_reopened_once()
    {
        var rca = FiveWhys(3);
        rca.Complete(Investigator, Now);

        rca.Complete(Investigator, Now).Error!.Code.ShouldBe("rca.completed");
        rca.Reopen().IsSuccess.ShouldBeTrue();
        rca.Status.ShouldBe(InvestigationStatus.InProgress);
        rca.CompletedAt.ShouldBeNull();
        rca.Reopen().IsFailure.ShouldBeTrue();
    }
}
