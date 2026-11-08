using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Patients.Domain;
using static HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys.JourneyClock;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>A stay in casualty, from arrival to disposition, each stage read from the emergency case.</summary>
public sealed class EmergencyJourneyTests
{
    /// <summary>Brought in by ambulance at 10:00, nothing else yet.</summary>
    private static readonly EmergencyFacts Arrived = new(
        VisitStatus.Open, At(10, 0), ArrivalMode.GovernmentAmbulance, null, null, null, null, [], null, null, null, null);

    [Fact]
    public void The_stages_follow_the_patient_from_arrival_to_disposition()
    {
        EmergencyJourney.Stages.Select(s => s.Key).ShouldBe(["arrived", "triaged", "seen-by-doctor", "investigations-done", "disposition"]);
    }

    [Fact]
    public void A_patient_just_arrived_waits_for_triage_and_the_time_counts_from_arrival()
    {
        var progress = EmergencyJourney.Progress(Arrived, Now);

        progress.Status.ShouldBe(JourneyStatus.Active);
        var current = progress.Current.ShouldNotBeNull();
        (current.Key, current.Detail, current.MinutesInStage).ShouldBe(("arrived", "Government ambulance", 60));
        Stage(progress, "triaged").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Pending), s => s.Detail.ShouldBe("Waiting for triage"));
        Stage(progress, "investigations-done").State.ShouldBe(StageState.Pending, "a test may still be ordered");
    }

    [Fact]
    public void A_triaged_patient_shows_the_level_until_the_doctor_sees_them()
    {
        var triaged = Arrived with { Level = 2, TriagedAt = At(10, 4) };

        var current = EmergencyJourney.Progress(triaged, Now).Current.ShouldNotBeNull();

        (current.Key, current.Detail, current.MinutesInStage).ShouldBe(("triaged", "ESI 2", 56));
    }

    [Fact]
    public void A_patient_resuscitated_before_triage_is_with_the_doctor_and_triage_was_passed_over()
    {
        var seen = Arrived with { DoctorSeenAt = At(10, 1), DoctorName = "Dr. Arjun Rao" };

        var progress = EmergencyJourney.Progress(seen, Now);

        var current = progress.Current.ShouldNotBeNull();
        (current.Key, current.Detail, current.MinutesInStage).ShouldBe(("seen-by-doctor", "Dr. Arjun Rao", 59));
        Stage(progress, "triaged").State.ShouldBe(StageState.Skipped);
    }

    [Fact]
    public void Investigations_run_alongside_and_never_hold_the_patient()
    {
        var waiting = Arrived with
        {
            Level = 3, TriagedAt = At(10, 5), DoctorSeenAt = At(10, 20), DoctorName = "Dr. Arjun Rao",
            Investigations = [new InvestigationFact(At(10, 50)), new InvestigationFact(null)],
        };

        var progress = EmergencyJourney.Progress(waiting, Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("seen-by-doctor");
        Stage(progress, "investigations-done").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Pending), s => s.Detail.ShouldBe("1 of 2 resulted"));
    }

    [Theory]
    [InlineData(EmergencyDisposition.Discharged, null, null, "Discharged")]
    [InlineData(EmergencyDisposition.Admitted, null, "IP-2026-000123", "Admitted · IP-2026-000123")]
    [InlineData(EmergencyDisposition.Admitted, null, null, "Admitted, bed not yet given")]
    [InlineData(EmergencyDisposition.ReferredOut, "District Hospital, Salem", null, "Referred to District Hospital, Salem")]
    [InlineData(EmergencyDisposition.Lama, null, null, "Left against medical advice")]
    [InlineData(EmergencyDisposition.Absconded, null, null, "Absconded")]
    [InlineData(EmergencyDisposition.Died, null, null, "Died")]
    public void The_disposition_ends_the_journey_and_says_where_the_patient_went(
        EmergencyDisposition disposition, string? referredTo, string? admission, string words)
    {
        var left = Arrived with
        {
            Level = 3, TriagedAt = At(10, 5), DoctorSeenAt = At(10, 20), DoctorName = "Dr. Arjun Rao",
            Disposition = disposition, DisposedAt = At(10, 55), ReferredTo = referredTo, AdmissionNumber = admission,
        };

        var progress = EmergencyJourney.Progress(left, Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        progress.Current.ShouldBeNull();
        Stage(progress, "disposition").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Done), s => s.At.ShouldBe(At(10, 55)), s => s.Detail.ShouldBe(words));
        Stage(progress, "investigations-done").State.ShouldBe(StageState.NotApplicable, "none was ordered before the patient left");
    }

    [Fact]
    public void A_patient_who_absconded_before_triage_went_without_it()
    {
        var gone = Arrived with { Disposition = EmergencyDisposition.Absconded, DisposedAt = At(10, 30) };

        var progress = EmergencyJourney.Progress(gone, Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        Stage(progress, "triaged").State.ShouldBe(StageState.Skipped);
        Stage(progress, "seen-by-doctor").State.ShouldBe(StageState.Skipped);
    }

    [Fact]
    public void A_visit_cancelled_at_the_desk_stops_the_journey_where_it_was()
    {
        var progress = EmergencyJourney.Progress(Arrived with { VisitStatus = VisitStatus.Cancelled }, Now);

        progress.Status.ShouldBe(JourneyStatus.Cancelled);
        Stage(progress, "arrived").State.ShouldBe(StageState.Done);
        Stage(progress, "triaged").State.ShouldBe(StageState.Stopped);
    }

    private static JourneyStage Stage(JourneyProgress progress, string key) => progress.Stages.Single(s => s.Key == key);
}
