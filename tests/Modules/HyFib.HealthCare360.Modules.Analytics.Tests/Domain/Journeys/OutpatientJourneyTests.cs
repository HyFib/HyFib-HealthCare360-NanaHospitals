using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using static HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys.JourneyClock;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>An out-patient visit's stages, each read from the record that proves it.</summary>
public sealed class OutpatientJourneyTests
{
    /// <summary>Checked in at 9:00 with token GM-012, nothing else yet.</summary>
    private static readonly OutpatientFacts CheckedIn = new(
        VisitStatus.Open, At(9, 0), null, new TokenFact("GM-012", null, null, null), [], [], [], new BillingFacts(1, 1, []));

    [Fact]
    public void The_stages_follow_the_visit_from_check_in_to_closing()
    {
        OutpatientJourney.Stages.Select(s => s.Key).ShouldBe(
            ["checked-in", "called", "in-consultation", "consultation-signed", "investigations-done", "dispensed", "billed", "paid", "closed"]);
    }

    [Fact]
    public void A_patient_just_checked_in_waits_to_be_called_and_the_time_counts_from_check_in()
    {
        var progress = OutpatientJourney.Progress(CheckedIn, Now);

        progress.Status.ShouldBe(JourneyStatus.Active);
        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("checked-in");
        current.Detail.ShouldBe("Token GM-012");
        current.MinutesInStage.ShouldBe(120);
        State(progress, "called").ShouldBe(StageState.Pending);

        // Before the doctor has seen the patient a test or a prescription may still come.
        State(progress, "investigations-done").ShouldBe(StageState.Pending);
        State(progress, "dispensed").ShouldBe(StageState.Pending);
        Stage(progress, "billed").Detail.ShouldBe("1 service not billed yet");
    }

    [Fact]
    public void A_consultation_paid_at_registration_is_billed_and_paid_while_the_patient_still_waits_for_the_doctor()
    {
        var paidAtTheDesk = CheckedIn with { Billing = new BillingFacts(2, 0, [new BillFact(At(9, 3), 0m, At(9, 5))]) };

        var progress = OutpatientJourney.Progress(paidAtTheDesk, Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("checked-in");
        Stage(progress, "billed").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Done), s => s.At.ShouldBe(At(9, 3)));
        Stage(progress, "paid").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Done), s => s.At.ShouldBe(At(9, 5)));
    }

    [Fact]
    public void A_called_patient_is_shown_with_the_room_until_the_consultation_starts()
    {
        var called = CheckedIn with { Token = new TokenFact("GM-012", At(10, 40), null, "Room 3") };

        var current = OutpatientJourney.Progress(called, Now).Current.ShouldNotBeNull();

        current.Key.ShouldBe("called");
        current.Detail.ShouldBe("Room 3");
        current.MinutesInStage.ShouldBe(20);
    }

    [Fact]
    public void The_consultation_starts_with_the_token_or_the_encounter_whichever_came_first()
    {
        var seen = CheckedIn with
        {
            Token = new TokenFact("GM-012", At(10, 0), At(10, 5), "Room 3"),
            Encounters = [new EncounterFact(At(10, 2), null)],
        };

        var progress = OutpatientJourney.Progress(seen, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("in-consultation");
        current.At.ShouldBe(At(10, 2));
        Stage(progress, "consultation-signed").Detail.ShouldBe("Note not signed yet");
    }

    [Fact]
    public void A_doctor_who_opens_the_encounter_without_calling_the_token_skips_the_call()
    {
        var seen = CheckedIn with { Encounters = [new EncounterFact(At(10, 0), null)] };

        var progress = OutpatientJourney.Progress(seen, Now);

        State(progress, "called").ShouldBe(StageState.Skipped);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("in-consultation");
    }

    [Fact]
    public void A_visit_checked_in_without_a_token_has_no_call()
    {
        var progress = OutpatientJourney.Progress(CheckedIn with { Token = null }, Now);

        Stage(progress, "called").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.NotApplicable),
            s => s.Detail.ShouldBe("No queue token"));
        Stage(progress, "checked-in").Detail.ShouldBeNull();
    }

    [Fact]
    public void Once_the_consultation_is_signed_with_nothing_ordered_or_prescribed_tests_and_medicines_do_not_apply()
    {
        var signed = Signed(CheckedIn) with { Billing = new BillingFacts(2, 0, [new BillFact(At(9, 3), 0m, At(9, 5))]) };

        var progress = OutpatientJourney.Progress(signed, Now);

        State(progress, "investigations-done").ShouldBe(StageState.NotApplicable);
        State(progress, "dispensed").ShouldBe(StageState.NotApplicable);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("consultation-signed");
        State(progress, "closed").ShouldBe(StageState.Pending);
    }

    [Fact]
    public void A_test_ordered_after_the_consultation_was_paid_makes_billing_pending_again_and_waits_for_its_report()
    {
        var ordered = Signed(CheckedIn) with
        {
            Investigations = [new InvestigationFact(null)],
            Billing = new BillingFacts(3, 1, [new BillFact(At(9, 3), 0m, At(9, 5))]),
        };

        var progress = OutpatientJourney.Progress(ordered, Now);

        Stage(progress, "billed").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Pending), s => s.Detail.ShouldBe("1 service not billed yet"));
        State(progress, "paid").ShouldBe(StageState.Pending);
        Stage(progress, "investigations-done").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("0 of 1 resulted"));
        progress.Current.ShouldNotBeNull().Key.ShouldBe("consultation-signed");
    }

    [Fact]
    public void A_bill_finalized_but_not_paid_says_what_is_due()
    {
        var billed = Signed(CheckedIn) with { Billing = new BillingFacts(3, 0, [new BillFact(At(10, 45), 650m, null)]) };

        var progress = OutpatientJourney.Progress(billed, Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("billed");
        Stage(progress, "paid").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Pending), s => s.Detail.ShouldBe("₹650.00 due"));
    }

    [Fact]
    public void Investigations_are_done_when_every_report_is_released_and_the_patient_is_then_there()
    {
        var resulted = Signed(CheckedIn) with
        {
            Investigations = [new InvestigationFact(At(10, 40)), new InvestigationFact(At(10, 50))],
            Billing = new BillingFacts(3, 0, [new BillFact(At(10, 10), 0m, At(10, 12))]),
        };

        var progress = OutpatientJourney.Progress(resulted, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("investigations-done");
        current.At.ShouldBe(At(10, 50));
        current.MinutesInStage.ShouldBe(10);
    }

    [Fact]
    public void Medicines_partly_given_at_the_pharmacy_are_still_to_be_dispensed()
    {
        var prescribed = Signed(CheckedIn) with { Prescriptions = [new DispensingFact(QueueEntryStatus.PartiallyDispensed, At(10, 30))] };

        var progress = OutpatientJourney.Progress(prescribed, Now);

        Stage(progress, "dispensed").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Pending), s => s.Detail.ShouldBe("Partly dispensed"));
    }

    [Fact]
    public void A_visit_closed_before_the_medicines_were_collected_is_finished_and_skips_dispensing()
    {
        var closed = Signed(CheckedIn) with
        {
            Status = VisitStatus.Completed,
            ClosedAt = At(10, 30),
            Prescriptions = [new DispensingFact(QueueEntryStatus.Pending, null)],
            Billing = new BillingFacts(2, 0, [new BillFact(At(9, 3), 0m, At(9, 5))]),
        };

        var progress = OutpatientJourney.Progress(closed, Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        progress.Current.ShouldBeNull();
        State(progress, "dispensed").ShouldBe(StageState.Skipped);
        Stage(progress, "closed").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Done), s => s.At.ShouldBe(At(10, 30)));
    }

    [Fact]
    public void A_cancelled_visit_stops_where_it_was()
    {
        var cancelled = CheckedIn with { Status = VisitStatus.Cancelled, ClosedAt = At(9, 20) };

        var progress = OutpatientJourney.Progress(cancelled, Now);

        progress.Status.ShouldBe(JourneyStatus.Cancelled);
        progress.Current.ShouldBeNull();
        State(progress, "checked-in").ShouldBe(StageState.Done);
        progress.Stages.Skip(1).ShouldAllBe(s => s.State == StageState.Stopped);
    }

    [Fact]
    public void A_free_follow_up_with_nothing_charged_has_nothing_to_bill_once_seen()
    {
        var free = Signed(CheckedIn) with { Billing = BillingFacts.None };

        var progress = OutpatientJourney.Progress(free, Now);

        State(progress, "billed").ShouldBe(StageState.NotApplicable);
        State(progress, "paid").ShouldBe(StageState.NotApplicable);
        State(OutpatientJourney.Progress(CheckedIn with { Billing = BillingFacts.None }, Now), "billed").ShouldBe(StageState.Pending);
    }

    private static OutpatientFacts Signed(OutpatientFacts facts) => facts with
    {
        Token = new TokenFact("GM-012", At(10, 0), At(10, 2), "Room 3"),
        Encounters = [new EncounterFact(At(10, 2), At(10, 20))],
    };

    private static JourneyStage Stage(JourneyProgress progress, string key) => progress.Stages.Single(s => s.Key == key);

    private static StageState State(JourneyProgress progress, string key) => Stage(progress, key).State;
}
