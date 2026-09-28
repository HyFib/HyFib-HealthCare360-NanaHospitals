using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class CapaTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid QualityManager = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static Capa NewCapa() => new()
    {
        CapaNumber = "NHCAPA260001",
        Type = CapaType.Corrective,
        Action = "Install night lights in ward corridors",
        OwnerUserId = Owner,
        DueDate = Today.AddDays(14),
        Status = CapaStatus.Open,
    };

    private static Capa Completed()
    {
        var capa = NewCapa();
        capa.Complete("Lights installed in all corridors", Now, Today).IsSuccess.ShouldBeTrue();
        return capa;
    }

    [Fact]
    public void Starting_moves_an_open_action_in_progress()
    {
        var capa = NewCapa();

        capa.Start(Now).IsSuccess.ShouldBeTrue();
        capa.Status.ShouldBe(CapaStatus.InProgress);
        capa.StartedAt.ShouldBe(Now);
        capa.Start(Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Completion_needs_a_note_and_schedules_the_effectiveness_check()
    {
        var capa = NewCapa();
        capa.Complete(" ", Now, Today).IsFailure.ShouldBeTrue();

        capa.Complete("Done", Now, Today).IsSuccess.ShouldBeTrue();

        capa.Status.ShouldBe(CapaStatus.Completed);
        capa.StartedAt.ShouldBe(Now);
        capa.EffectivenessCheckDue.ShouldBe(Today.AddDays(Capa.DefaultEffectivenessDelayDays));
    }

    [Fact]
    public void The_owner_cannot_verify_their_own_action()
    {
        var result = Completed().Verify(true, "Falls reduced", Owner, Now);

        result.Error!.Code.ShouldBe("capa.self_verification");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void An_effective_action_is_verified()
    {
        var capa = Completed();

        capa.Verify(true, "No night-time falls in 30 days", QualityManager, Now).IsSuccess.ShouldBeTrue();

        capa.Status.ShouldBe(CapaStatus.Verified);
        capa.IsEffective.ShouldBe(true);
        capa.VerifiedByUserId.ShouldBe(QualityManager);
    }

    [Fact]
    public void An_ineffective_action_goes_back_for_rework()
    {
        var capa = Completed();

        capa.Verify(false, "Two more falls in the same corridor", QualityManager, Now).IsSuccess.ShouldBeTrue();

        capa.Status.ShouldBe(CapaStatus.InProgress);
        capa.ReworkCount.ShouldBe(1);
        capa.CompletedAt.ShouldBeNull();
        capa.EffectivenessCheckDue.ShouldBeNull();
    }

    [Fact]
    public void Verification_needs_evidence_and_a_completed_action()
    {
        NewCapa().Verify(true, "ok", QualityManager, Now).Error!.Code.ShouldBe("capa.not_completed");
        Completed().Verify(true, " ", QualityManager, Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Only_open_actions_can_be_cancelled()
    {
        NewCapa().Cancel("Superseded").IsSuccess.ShouldBeTrue();
        Completed().Cancel("Too late").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Open_actions_past_their_due_date_are_overdue()
    {
        var capa = NewCapa();

        capa.IsOverdue(Today).ShouldBeFalse();
        capa.IsOverdue(Today.AddDays(15)).ShouldBeTrue();
        Completed().IsOverdue(Today.AddDays(60)).ShouldBeFalse();
    }
}
