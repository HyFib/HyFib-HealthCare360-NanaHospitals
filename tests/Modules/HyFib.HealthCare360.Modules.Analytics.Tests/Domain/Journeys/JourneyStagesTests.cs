using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using static HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys.JourneyClock;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>How the pipeline turns what the records say about each stage into where the patient is.</summary>
public sealed class JourneyStagesTests
{
    private static readonly StageDefinition Arrived = new("arrived", "Arrived", StageRole.Core);
    private static readonly StageDefinition Called = new("called", "Called", StageRole.Optional);
    private static readonly StageDefinition Seen = new("seen", "Seen", StageRole.Core);
    private static readonly StageDefinition Paid = new("paid", "Paid", StageRole.Parallel);
    private static readonly StageDefinition Left = new("left", "Left", StageRole.Core);

    [Fact]
    public void The_patient_is_in_the_last_stage_reached_before_the_first_that_holds_them_up()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Active,
            [Arrived.Reached(At(9, 0)), Called.Reached(At(9, 40)), Seen.Open(), Paid.Open(), Left.Open()], Now);

        States(progress).ShouldBe([StageState.Done, StageState.Current, StageState.Pending, StageState.Pending, StageState.Pending]);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("called");
        progress.Current.MinutesInStage.ShouldBe(80, "called at 9:40, looked at 11:00");
        progress.Stages.Where(s => s.State != StageState.Current).ShouldAllBe(s => s.MinutesInStage == null);
    }

    [Fact]
    public void A_parallel_stage_reached_early_shows_done_without_moving_the_patient_past_the_stage_that_holds_them()
    {
        // The consultation was paid at registration; the patient still waits to be called.
        var progress = JourneyStages.Resolve(JourneyStatus.Active,
            [Arrived.Reached(At(9, 0)), Called.Open(), Seen.Open(), Paid.Reached(At(9, 5)), Left.Open()], Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("arrived");
        progress.Current.MinutesInStage.ShouldBe(120);
        States(progress).ShouldBe([StageState.Current, StageState.Pending, StageState.Pending, StageState.Done, StageState.Pending]);
    }

    [Fact]
    public void A_parallel_stage_still_to_come_stays_pending_while_the_patient_moves_on()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Active,
            [Arrived.Reached(At(9, 0)), Called.Reached(At(9, 30)), Seen.Reached(At(9, 35)), Paid.Open("₹500.00 due"), Left.Open()], Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("seen");
        progress.Stages.Single(s => s.Key == "paid").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("₹500.00 due"));
    }

    [Fact]
    public void Of_the_stages_reached_the_patient_is_in_the_one_reached_last()
    {
        // Paid at 10:30 after being seen at 9:35: the latest thing that happened is where the patient is.
        var progress = JourneyStages.Resolve(JourneyStatus.Active,
            [Arrived.Reached(At(9, 0)), Called.Reached(At(9, 30)), Seen.Reached(At(9, 35)), Paid.Reached(At(10, 30)), Left.Open()], Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("paid");
        progress.Current.MinutesInStage.ShouldBe(30);
        progress.Stages.Single(s => s.Key == "seen").State.ShouldBe(StageState.Done);
    }

    [Fact]
    public void An_optional_stage_a_later_stage_went_past_is_skipped()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Active,
            [Arrived.Reached(At(9, 0)), Called.Open(), Seen.Reached(At(9, 30)), Paid.Open(), Left.Open()], Now);

        progress.Stages.Single(s => s.Key == "called").State.ShouldBe(StageState.Skipped);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("seen");
    }

    [Fact]
    public void Only_a_stage_that_holds_the_position_skips_an_optional_one()
    {
        // Paying (parallel) does not mean the call was skipped: the patient is still waiting for it.
        var progress = JourneyStages.Resolve(JourneyStatus.Active,
            [Arrived.Reached(At(9, 0)), Called.Open(), Seen.Open(), Paid.Reached(At(9, 5)), Left.Open()], Now);

        progress.Stages.Single(s => s.Key == "called").State.ShouldBe(StageState.Pending);
    }

    [Fact]
    public void A_stage_whose_time_is_not_stored_counts_as_after_the_stages_before_it_and_has_no_minutes()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Active, [Arrived.Reached(At(9, 0)), Seen.Reached(null, "Slot at 2 PM"), Left.Open()], Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("seen");
        current.At.ShouldBeNull();
        current.MinutesInStage.ShouldBeNull("nothing stores when it happened, so the time spent in it is not made up");
        current.Detail.ShouldBe("Slot at 2 PM");
    }

    [Fact]
    public void Two_stages_reached_at_the_same_moment_put_the_patient_in_the_later_one()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Active, [Arrived.Reached(At(9, 0)), Seen.Reached(At(9, 0)), Left.Open()], Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("seen");
    }

    [Fact]
    public void A_stage_that_does_not_apply_is_shown_so_and_never_holds_the_patient_up()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Active,
            [Arrived.Reached(At(9, 0)), Called.NotApplicable("No queue token"), Seen.Reached(At(9, 20)), Left.Open()], Now);

        progress.Stages.Single(s => s.Key == "called").State.ShouldBe(StageState.NotApplicable);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("seen");
    }

    [Fact]
    public void A_finished_journey_skips_what_it_went_without_and_has_no_current_stage()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Completed,
            [Arrived.Reached(At(9, 0)), Called.Reached(At(9, 30)), Seen.Reached(At(9, 35)), Paid.Open(), Left.Reached(At(10, 0))], Now);

        progress.Current.ShouldBeNull();
        States(progress).ShouldBe([StageState.Done, StageState.Done, StageState.Done, StageState.Skipped, StageState.Done]);
        progress.Stages.ShouldAllBe(s => s.MinutesInStage == null);
    }

    [Fact]
    public void A_cancelled_journey_stops_at_the_stages_it_had_not_reached()
    {
        var progress = JourneyStages.Resolve(JourneyStatus.Cancelled,
            [Arrived.Reached(At(9, 0)), Called.NotApplicable(), Seen.Open(), Paid.Open(), Left.Open()], Now);

        progress.Current.ShouldBeNull();
        States(progress).ShouldBe([StageState.Done, StageState.NotApplicable, StageState.Stopped, StageState.Stopped, StageState.Stopped]);
    }

    [Fact]
    public void Minutes_in_a_stage_reached_in_the_future_of_the_clock_are_never_negative()
    {
        // A late entry recorded a few seconds ahead of this server's clock.
        var progress = JourneyStages.Resolve(JourneyStatus.Active, [Arrived.Reached(Now.AddSeconds(20)), Left.Open()], Now);

        progress.Current.ShouldNotBeNull().MinutesInStage.ShouldBe(0);
    }

    private static StageState[] States(JourneyProgress progress) => [.. progress.Stages.Select(s => s.State)];
}
