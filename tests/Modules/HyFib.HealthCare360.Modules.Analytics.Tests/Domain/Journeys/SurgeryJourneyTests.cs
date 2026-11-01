using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using static HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys.JourneyClock;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>An operation's stages, the OT day-of-surgery stepper's, from request to leaving the theatre with the charges posted.</summary>
public sealed class SurgeryJourneyTests
{
    /// <summary>A knee replacement requested yesterday at 16:00, given the 14:00 slot in OT-2 today; nothing done on the day yet.</summary>
    private static readonly SurgeryFacts Booked = new(
        SurgeryStatus.Scheduled,
        DaysAgo(1, 16, 0),
        At(14, 0),
        "OT-2",
        null,
        PreOpChecklist.NewEntries(),
        null,
        null,
        null,
        null,
        null,
        null,
        false);

    [Fact]
    public void The_stages_are_the_day_of_surgery_without_a_recovery_stage_nothing_records()
    {
        SurgeryJourney.Stages.Select(s => s.Key).ShouldBe(
            ["requested", "scheduled", "pre-op", "received", "sign-in", "time-out", "incision", "sign-out", "out-and-billed"]);
    }

    [Fact]
    public void A_scheduled_operation_is_at_its_slot_whose_booking_time_is_not_stored()
    {
        var progress = SurgeryJourney.Progress(Booked, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("scheduled");
        current.At.ShouldBeNull("when the slot was given is not stored, so it is not made up");
        current.MinutesInStage.ShouldBeNull();
        current.Detail.ShouldBe("Slot 31 Oct 2026, 02:00 PM IST in OT-2");
        Stage(progress, "pre-op").Detail.ShouldBe("0 of 10 checks done, consent not recorded");
    }

    [Fact]
    public void A_requested_operation_waits_for_a_theatre_and_time()
    {
        var progress = SurgeryJourney.Progress(Booked with { Status = SurgeryStatus.Requested, ScheduledStart = null, Theatre = null }, Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("requested");
        Stage(progress, "scheduled").Detail.ShouldBe("Waiting for a theatre and time");
    }

    [Fact]
    public void A_postponed_operation_needs_a_new_slot_whatever_slot_it_had()
    {
        var postponed = Booked with { Status = SurgeryStatus.Postponed, PostponeReason = "Patient febrile" };

        var progress = SurgeryJourney.Progress(postponed, Now);

        progress.Status.ShouldBe(JourneyStatus.Active);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("requested");
        Stage(progress, "scheduled").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("Postponed: Patient febrile"));
    }

    [Fact]
    public void The_pre_op_checks_and_consent_are_done_with_the_last_item_recorded()
    {
        var progress = SurgeryJourney.Progress(Booked with { PreOpChecks = PreOpDone(At(10, 15)) }, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("pre-op");
        current.At.ShouldBe(At(10, 15));
        current.MinutesInStage.ShouldBe(45);
    }

    [Fact]
    public void Consent_recorded_with_other_checks_outstanding_is_said()
    {
        var consented = PreOpChecklist.NewEntries()
            .Select(e => e.Key == "consent" ? e with { State = ChecklistItemState.Done, RecordedAt = At(9, 0) } : e)
            .ToList();

        Stage(SurgeryJourney.Progress(Booked with { PreOpChecks = consented }, Now), "pre-op").Detail.ShouldBe("1 of 10 checks done");
    }

    [Fact]
    public void Receiving_the_patient_in_pre_op_is_skipped_when_the_sign_in_is_done_at_the_table()
    {
        var signedIn = Booked with { PreOpChecks = PreOpDone(At(10, 15)), SignInAt = At(10, 50) };

        var progress = SurgeryJourney.Progress(signedIn, Now);

        State(progress, "received").ShouldBe(StageState.Skipped);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("sign-in");
    }

    [Fact]
    public void An_operation_under_way_is_at_the_incision()
    {
        var underWay = Booked with
        {
            Status = SurgeryStatus.InProgress,
            PreOpChecks = PreOpDone(At(9, 0)),
            ReceivedInPreOpAt = At(9, 30),
            SignInAt = At(9, 50),
            TimeOutAt = At(10, 5),
            StartedAt = At(10, 10),
        };

        var progress = SurgeryJourney.Progress(underWay, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("incision");
        current.MinutesInStage.ShouldBe(50);
        State(progress, "received").ShouldBe(StageState.Done);
    }

    [Fact]
    public void A_patient_out_of_theatre_whose_charges_failed_is_not_billed_yet()
    {
        var unbilled = Completed() with { ChargesFailed = true };

        var progress = SurgeryJourney.Progress(unbilled, Now);

        progress.Status.ShouldBe(JourneyStatus.Active);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("sign-out");
        Stage(progress, "out-and-billed").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("Out of theatre; the OT charges were not posted"));
    }

    [Fact]
    public void An_operation_finished_with_the_charges_posted_is_complete()
    {
        var progress = SurgeryJourney.Progress(Completed(), Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        progress.Current.ShouldBeNull();
        Stage(progress, "out-and-billed").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Done), s => s.At.ShouldBe(At(10, 50)));
    }

    [Fact]
    public void A_cancelled_booking_stops_where_it_was()
    {
        var progress = SurgeryJourney.Progress(Booked with { Status = SurgeryStatus.Cancelled }, Now);

        progress.Status.ShouldBe(JourneyStatus.Cancelled);
        State(progress, "scheduled").ShouldBe(StageState.Done, "it held a slot when it was cancelled");
        progress.Stages.Skip(2).ShouldAllBe(s => s.State == StageState.Stopped);
    }

    private static List<ChecklistEntry> PreOpDone(DateTimeOffset at) =>
        [.. PreOpChecklist.NewEntries().Select(e => e with { State = ChecklistItemState.Done, RecordedAt = at, RecordedByName = "Sr. Lakshmi" })];

    private static SurgeryFacts Completed() => Booked with
    {
        Status = SurgeryStatus.Completed,
        PreOpChecks = PreOpDone(At(8, 0)),
        ReceivedInPreOpAt = At(8, 30),
        SignInAt = At(8, 50),
        TimeOutAt = At(9, 5),
        StartedAt = At(9, 10),
        SignOutAt = At(10, 40),
        CompletedAt = At(10, 50),
    };

    private static JourneyStage Stage(JourneyProgress progress, string key) => progress.Stages.Single(s => s.Key == key);

    private static StageState State(JourneyProgress progress, string key) => Stage(progress, key).State;
}
