using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using static HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys.JourneyClock;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>An in-patient stay's stages, from admission until the bed the patient left is clean.</summary>
public sealed class InpatientJourneyTests
{
    /// <summary>
    /// Admitted two days ago at 10:00 from his OPD visit to the male general ward; the consultant's first round was that
    /// evening.
    /// </summary>
    private static readonly InpatientFacts InTheWard = new(
        AdmissionStatus.Admitted,
        DaysAgo(2, 10, 0),
        AdmissionSource.OutPatient,
        "NHV260000076",
        DaysAgo(2, 10, 0),
        "General Ward — Male · GWM-07",
        DaysAgo(2, 18, 30),
        [],
        null,
        null,
        false,
        new ClearanceFacts(true, 4, [], null),
        null,
        null,
        null);

    [Fact]
    public void The_stages_follow_the_stay_until_the_bed_is_clean_again()
    {
        InpatientJourney.Stages.Select(s => s.Key).ShouldBe(
            ["admitted", "bedded", "rounds-and-nursing", "investigations-complete", "discharge-initiated", "summary-signed", "cleared", "discharged",
             "bed-cleaned"]);
    }

    [Fact]
    public void A_patient_in_the_ward_is_under_rounds_and_nursing_from_the_first_round()
    {
        var progress = InpatientJourney.Progress(InTheWard, Now);

        progress.Status.ShouldBe(JourneyStatus.Active);
        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("rounds-and-nursing");
        current.MinutesInStage.ShouldBe((int)(Now - DaysAgo(2, 18, 30)).TotalMinutes);
        Stage(progress, "bedded").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Done),
            s => s.Detail.ShouldBe("General Ward — Male · GWM-07"));

        // No test ordered yet: nothing to complete.
        State(progress, "investigations-complete").ShouldBe(StageState.NotApplicable);
        Stage(progress, "cleared").Detail.ShouldBe("4 services not billed yet");
    }

    [Theory]
    [InlineData(AdmissionSource.OutPatient, "NHV260000076", "From OPD visit NHV260000076")]
    [InlineData(AdmissionSource.Emergency, "NHV260000081", "From casualty visit NHV260000081")]
    [InlineData(AdmissionSource.OutPatient, null, "Advised in OPD")]
    [InlineData(AdmissionSource.Emergency, null, "From casualty / ER")]
    [InlineData(AdmissionSource.Referral, null, "Referred by a doctor or hospital")]
    [InlineData(AdmissionSource.Direct, null, "Direct admission")]
    public void The_admission_says_where_the_patient_came_from_and_names_the_visit_it_links(
        AdmissionSource source, string? visitNumber, string from)
    {
        var admitted = InTheWard with { Source = source, SourceVisitNumber = visitNumber };

        Stage(InpatientJourney.Progress(admitted, Now), "admitted").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Done),
            s => s.Detail.ShouldBe(from));
    }

    [Fact]
    public void A_patient_just_admitted_and_put_in_a_bed_waits_for_the_first_round()
    {
        var admitted = InTheWard with { AdmittedAt = At(10, 0), BeddedAt = At(10, 0), FirstRoundAt = null };

        var progress = InpatientJourney.Progress(admitted, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("bedded");
        current.MinutesInStage.ShouldBe(60);
        State(progress, "rounds-and-nursing").ShouldBe(StageState.Pending);
    }

    [Fact]
    public void Investigations_are_complete_when_every_report_of_the_stay_is_released()
    {
        var resulted = InTheWard with { Investigations = [new InvestigationFact(DaysAgo(1, 11, 0)), new InvestigationFact(At(9, 30))] };

        var current = InpatientJourney.Progress(resulted, Now).Current.ShouldNotBeNull();

        current.Key.ShouldBe("investigations-complete");
        current.At.ShouldBe(At(9, 30), "the last report came at 9:30");
        current.MinutesInStage.ShouldBe(90);
    }

    [Fact]
    public void A_stay_whose_discharge_began_before_any_round_or_nursing_note_was_recorded_skips_rounds_and_nursing()
    {
        var dayCare = InTheWard with
        {
            Status = AdmissionStatus.DischargeInitiated,
            AdmittedAt = At(7, 0),
            BeddedAt = At(7, 0),
            FirstRoundAt = null,
            DischargeInitiatedAt = At(9, 0),
        };

        var progress = InpatientJourney.Progress(dayCare, Now);

        State(progress, "rounds-and-nursing").ShouldBe(StageState.Skipped);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("discharge-initiated");
    }

    [Fact]
    public void Tests_still_out_when_the_discharge_begins_stay_pending_while_the_patient_moves_on()
    {
        var discharging = InTheWard with
        {
            Status = AdmissionStatus.DischargeInitiated,
            Investigations = [new InvestigationFact(DaysAgo(1, 9, 0)), new InvestigationFact(null)],
            DischargeInitiatedAt = At(9, 30),
            SummaryDrafted = true,
        };

        var progress = InpatientJourney.Progress(discharging, Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("discharge-initiated");
        Stage(progress, "investigations-complete").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("1 of 2 resulted"));
        Stage(progress, "summary-signed").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("Draft not signed yet"));
    }

    [Fact]
    public void Billing_clears_the_stay_once_the_final_bill_is_paid()
    {
        var cleared = InTheWard with
        {
            Status = AdmissionStatus.DischargeInitiated,
            DischargeInitiatedAt = At(9, 0),
            SummarySignedAt = At(9, 40),
            Clearance = new ClearanceFacts(true, 0, [new BillFact(At(10, 0), 0m, At(10, 15))], null),
        };

        var progress = InpatientJourney.Progress(cleared, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("cleared");
        current.At.ShouldBe(At(10, 15), "the patient's share was paid at 10:15, after the final bill at 10:00");
    }

    [Fact]
    public void A_final_bill_with_money_still_due_does_not_clear_the_stay()
    {
        var due = InTheWard with
        {
            Status = AdmissionStatus.DischargeInitiated,
            DischargeInitiatedAt = At(9, 0),
            Clearance = new ClearanceFacts(true, 0, [new BillFact(At(10, 0), 12_500m, null)], null),
        };

        Stage(InpatientJourney.Progress(due, Now), "cleared").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("₹12,500.00 due"));
        Stage(InpatientJourney.Progress(due with { Clearance = new ClearanceFacts(true, 0, [], null) }, Now), "cleared").Detail
            .ShouldBe("No bill yet");
    }

    [Fact]
    public void A_stay_whose_services_all_went_on_interim_bills_is_cleared_when_the_last_of_them_is_settled()
    {
        // Billing clears it so (DischargeClearanceRules): nothing left to bill and nothing due, with or without a final bill.
        var interim = InTheWard with
        {
            Status = AdmissionStatus.DischargeInitiated,
            DischargeInitiatedAt = At(9, 0),
            SummarySignedAt = At(9, 40),
            Clearance = new ClearanceFacts(true, 0, [new BillFact(DaysAgo(2, 18, 0), 0m, DaysAgo(2, 18, 30)), new BillFact(At(9, 30), 0m, At(10, 5))], null),
        };

        var current = InpatientJourney.Progress(interim, Now).Current.ShouldNotBeNull();
        current.Key.ShouldBe("cleared");
        current.At.ShouldBe(At(10, 5), "the second interim bill was settled at 10:05");
    }

    [Fact]
    public void A_discharge_with_dues_approved_by_management_clears_billing()
    {
        var approved = InTheWard with
        {
            Status = AdmissionStatus.DischargeInitiated,
            DischargeInitiatedAt = At(9, 0),
            Clearance = new ClearanceFacts(true, 0, [new BillFact(At(10, 0), 3_000m, null)], At(10, 20)),
        };

        Stage(InpatientJourney.Progress(approved, Now), "cleared").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Current),
            s => s.At.ShouldBe(At(10, 20)),
            s => s.Detail.ShouldBe("Discharge with dues approved"));
    }

    [Fact]
    public void A_patient_who_left_against_medical_advice_skips_clearance_and_the_stay_waits_for_the_bed_to_be_cleaned()
    {
        var lama = InTheWard with
        {
            Status = AdmissionStatus.Lama,
            DischargeInitiatedAt = At(10, 0),
            DischargedAt = At(10, 0),
            DischargeType = DischargeType.Lama,
            Cleaning = new CleaningFact(At(10, 0), null),
        };

        var progress = InpatientJourney.Progress(lama, Now);

        progress.Status.ShouldBe(JourneyStatus.Active);
        State(progress, "cleared").ShouldBe(StageState.Skipped);
        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("discharged");
        current.Detail.ShouldBe("Left against medical advice (LAMA)");
        Stage(progress, "bed-cleaned").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("Waiting for housekeeping"));
    }

    [Fact]
    public void The_stay_is_finished_when_the_bed_the_patient_left_is_clean()
    {
        var gone = Discharged(new CleaningFact(At(10, 0), At(10, 40)));

        var progress = InpatientJourney.Progress(gone, Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        progress.Current.ShouldBeNull();
        Stage(progress, "bed-cleaned").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Done), s => s.At.ShouldBe(At(10, 40)));
    }

    [Fact]
    public void A_bed_not_left_for_cleaning_has_no_cleaning_and_the_stay_is_finished_at_discharge()
    {
        var progress = InpatientJourney.Progress(Discharged(cleaning: null), Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        State(progress, "bed-cleaned").ShouldBe(StageState.NotApplicable);
    }

    [Fact]
    public void A_cancelled_admission_stops_where_it_was()
    {
        var progress = InpatientJourney.Progress(InTheWard with { Status = AdmissionStatus.Cancelled }, Now);

        progress.Status.ShouldBe(JourneyStatus.Cancelled);
        progress.Current.ShouldBeNull();
        State(progress, "discharge-initiated").ShouldBe(StageState.Stopped);
        State(progress, "rounds-and-nursing").ShouldBe(StageState.Done);
    }

    [Fact]
    public void A_stay_with_nothing_charged_has_no_clearance_to_wait_for()
    {
        var progress = InpatientJourney.Progress(InTheWard with { Clearance = ClearanceFacts.None }, Now);

        State(progress, "cleared").ShouldBe(StageState.NotApplicable);
    }

    private static InpatientFacts Discharged(CleaningFact? cleaning) => InTheWard with
    {
        Status = AdmissionStatus.Discharged,
        DischargeInitiatedAt = At(8, 0),
        SummarySignedAt = At(8, 30),
        Clearance = new ClearanceFacts(true, 0, [new BillFact(At(9, 0), 0m, At(9, 20))], null),
        DischargedAt = At(10, 0),
        DischargeType = DischargeType.Normal,
        Cleaning = cleaning,
    };

    private static JourneyStage Stage(JourneyProgress progress, string key) => progress.Stages.Single(s => s.Key == key);

    private static StageState State(JourneyProgress progress, string key) => Stage(progress, key).State;
}
