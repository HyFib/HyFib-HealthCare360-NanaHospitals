using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Quality;

/// <summary>
/// External quality assessment rounds (NABL 112, ISO 15189 7.3.7.3): the z-score of ISO 13528, its bands, and an
/// unacceptable round accepted only with its corrective action linked.
/// </summary>
public sealed class EqasRoundTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(105, 100, 5, 1.00)]
    [InlineData(90, 100, 4, -2.50)]
    [InlineData(100.333, 100, 0.1, 3.33)]
    [InlineData(99.995, 100, 1, -0.01)]
    public void The_z_score_is_the_distance_from_the_assigned_value_in_its_sds(decimal reported, decimal assigned, decimal sd, decimal z) =>
        EqasRound.Z(reported, assigned, sd).ShouldBe(z);

    [Theory]
    [InlineData(0, EqasPerformance.Acceptable)]
    [InlineData(2.00, EqasPerformance.Acceptable)]
    [InlineData(-2.00, EqasPerformance.Acceptable)]
    [InlineData(2.01, EqasPerformance.Warning)]
    [InlineData(-3.00, EqasPerformance.Warning)]
    [InlineData(3.01, EqasPerformance.Unacceptable)]
    [InlineData(-4.5, EqasPerformance.Unacceptable)]
    public void Above_two_sds_warns_and_above_three_is_unacceptable(decimal z, EqasPerformance band) => EqasRound.Judge(z).ShouldBe(band);

    [Fact]
    public void An_unacceptable_round_is_accepted_only_once_its_corrective_action_is_linked()
    {
        var round = Reported(140);
        round.Evaluate(100, 10, "Peer: enzymatic", Today, Today).IsSuccess.ShouldBeTrue();
        (round.ZScore, round.Performance, round.NeedsCapa).ShouldBe((4.00m, EqasPerformance.Unacceptable, true));

        round.Accept("Calibrator lot changed; recalibrated", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("eqas.capa_required");
        round.LinkCapa(Guid.CreateVersion7(), "MAINCAPA260012").IsSuccess.ShouldBeTrue();
        round.NeedsCapa.ShouldBeFalse();
        round.Accept(null, Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("validation", "what was found is said for a round that was not acceptable");
        round.Accept("Calibrator lot changed; recalibrated", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        round.Accept("again", null, Now).Error!.Code.ShouldBe("eqas.accepted");
    }

    [Fact]
    public void An_acceptable_round_is_accepted_without_a_note_or_a_corrective_action()
    {
        var round = Reported(101);
        round.Evaluate(100, 2, null, Today, Today).IsSuccess.ShouldBeTrue();
        (round.ZScore, round.Performance).ShouldBe((0.50m, EqasPerformance.Acceptable));
        round.Accept(null, null, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_round_is_evaluated_only_on_a_reported_result_and_the_result_does_not_change_once_judged()
    {
        var round = new EqasRound { RoundCode = "2026/11-2", DueDate = Today };
        round.Evaluate(100, 5, null, Today, Today).Error!.Code.ShouldBe("eqas.not_reported");
        round.Report(104, Today, null, Today).IsSuccess.ShouldBeTrue();
        round.Evaluate(100, 0, null, Today, Today).IsFailure.ShouldBeTrue("an SD of zero judges nothing");
        round.Evaluate(100, 5, null, Today, Today).IsSuccess.ShouldBeTrue();
        round.Report(100, Today, null, Today).Error!.Code.ShouldBe("eqas.evaluated");
    }

    [Fact]
    public void A_round_not_reported_by_its_due_date_is_overdue()
    {
        var round = new EqasRound { RoundCode = "2026/10-1", DueDate = Today.AddDays(-1) };
        round.IsOverdue(Today).ShouldBeTrue();
        round.Report(5.1m, Today, null, Today);
        round.IsOverdue(Today).ShouldBeFalse();
        new EqasRound { DueDate = Today }.IsOverdue(Today).ShouldBeFalse("due today is not yet late");
    }

    private static EqasRound Reported(decimal value)
    {
        var round = new EqasRound { RoundCode = "2026/11-1", DueDate = Today };
        round.Report(value, Today, null, Today).IsSuccess.ShouldBeTrue();
        return round;
    }
}
