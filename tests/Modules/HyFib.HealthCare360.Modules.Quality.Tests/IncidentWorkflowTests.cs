using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class IncidentWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly IncidentProgress RcaDone = new(true, 0, 0);

    private static Incident Incident(IncidentStatus status, IncidentSeverity severity = IncidentSeverity.Mild)
    {
        var incident = new Incident { Status = status };
        incident.Classify(IncidentType.PatientFall, severity, null, false);
        return incident;
    }

    [Theory]
    [InlineData(IncidentStatus.Reported, IncidentStatus.UnderReview)]
    [InlineData(IncidentStatus.UnderReview, IncidentStatus.Investigation)]
    [InlineData(IncidentStatus.Closed, IncidentStatus.UnderReview)]
    public void Follows_the_review_path(IncidentStatus from, IncidentStatus to) =>
        IncidentWorkflow.CanMove(from, to, false, IncidentProgress.None, "note").IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(IncidentStatus.Reported, IncidentStatus.Investigation)]
    [InlineData(IncidentStatus.Reported, IncidentStatus.CapaInProgress)]
    [InlineData(IncidentStatus.Closed, IncidentStatus.Investigation)]
    [InlineData(IncidentStatus.Investigation, IncidentStatus.Reported)]
    public void Rejects_steps_off_the_path(IncidentStatus from, IncidentStatus to) =>
        IncidentWorkflow.CanMove(from, to, false, RcaDone, "note").Error!.Code.ShouldBe("incident.invalid_transition");

    [Fact]
    public void Closing_needs_an_outcome_summary()
    {
        IncidentWorkflow.CanMove(IncidentStatus.UnderReview, IncidentStatus.Closed, false, IncidentProgress.None, " ").IsFailure.ShouldBeTrue();
        IncidentWorkflow.CanMove(IncidentStatus.UnderReview, IncidentStatus.Closed, false, IncidentProgress.None, "No harm; staff counselled").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Leaving_an_investigation_needs_a_completed_rca()
    {
        IncidentWorkflow.CanMove(IncidentStatus.Investigation, IncidentStatus.Closed, false, IncidentProgress.None, "done").Error!.Code.ShouldBe("incident.rca_incomplete");
        IncidentWorkflow.CanMove(IncidentStatus.Investigation, IncidentStatus.Closed, false, RcaDone, "done").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Capa_stage_needs_at_least_one_capa()
    {
        IncidentWorkflow.CanMove(IncidentStatus.UnderReview, IncidentStatus.CapaInProgress, false, IncidentProgress.None, null).Error!.Code.ShouldBe("incident.no_capa");
        IncidentWorkflow.CanMove(IncidentStatus.UnderReview, IncidentStatus.CapaInProgress, false, new IncidentProgress(false, 1, 1), null).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Open_capas_block_closure()
    {
        IncidentWorkflow.CanMove(IncidentStatus.CapaInProgress, IncidentStatus.Closed, false, new IncidentProgress(true, 3, 1), "done").Error!.Code.ShouldBe("incident.capa_open");
        IncidentWorkflow.CanMove(IncidentStatus.CapaInProgress, IncidentStatus.Closed, false, new IncidentProgress(true, 3, 0), "done").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Sentinel_events_close_only_after_a_completed_rca()
    {
        IncidentWorkflow.CanMove(IncidentStatus.UnderReview, IncidentStatus.Closed, true, IncidentProgress.None, "done").Error!.Code.ShouldBe("incident.sentinel_needs_rca");
        IncidentWorkflow.CanMove(IncidentStatus.UnderReview, IncidentStatus.Closed, true, RcaDone, "done").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_duplicate_sentinel_report_can_be_closed_straight_away()
    {
        IncidentWorkflow.CanMove(IncidentStatus.Reported, IncidentStatus.Closed, true, IncidentProgress.None, "Duplicate of NHIR2600012").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Moving_records_review_start_and_closure()
    {
        var incident = Incident(IncidentStatus.Reported);

        incident.MoveTo(IncidentStatus.UnderReview, null, IncidentProgress.None, Now).IsSuccess.ShouldBeTrue();
        incident.ReviewStartedAt.ShouldBe(Now);

        incident.MoveTo(IncidentStatus.Closed, " Minor fall, no injury ", IncidentProgress.None, Now.AddDays(1)).IsSuccess.ShouldBeTrue();
        incident.Status.ShouldBe(IncidentStatus.Closed);
        incident.ClosedAt.ShouldBe(Now.AddDays(1));
        incident.ClosureNote.ShouldBe("Minor fall, no injury");
    }

    [Fact]
    public void Reopening_needs_a_reason_and_is_counted()
    {
        var incident = Incident(IncidentStatus.Closed);
        incident.ClosedAt = Now;

        incident.MoveTo(IncidentStatus.UnderReview, null, IncidentProgress.None, Now).IsFailure.ShouldBeTrue();
        incident.MoveTo(IncidentStatus.UnderReview, "Family raised new concerns", IncidentProgress.None, Now).IsSuccess.ShouldBeTrue();

        incident.ReopenCount.ShouldBe(1);
        incident.ClosedAt.ShouldBeNull();
    }

    [Fact]
    public void Next_states_are_published_for_the_ui()
    {
        IncidentWorkflow.NextStates(IncidentStatus.UnderReview).ShouldBe([IncidentStatus.Investigation, IncidentStatus.CapaInProgress, IncidentStatus.Closed]);
        IncidentWorkflow.NextStates(IncidentStatus.Closed).ShouldBe([IncidentStatus.UnderReview]);
    }
}
