using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using static HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys.JourneyClock;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>A lab accession's stages, from the doctor's order to the doctor's review of the report.</summary>
public sealed class LabJourneyTests
{
    /// <summary>A CRP the OPD doctor ordered at 10:00 for an out-patient, not yet paid.</summary>
    private static readonly LabTestFact Crp = new(false, false, null, null, null, null, null, false, null);

    private static readonly LabFacts Ordered = new(LabOrderStatus.Ordered, At(10, 0), true, true, [Crp], null);

    [Fact]
    public void The_stages_run_from_the_order_to_the_doctors_review()
    {
        LabJourney.Stages.Select(s => s.Key).ShouldBe(["ordered", "payment-cleared", "collected", "received", "resulted", "verified", "released", "reviewed"]);
    }

    [Fact]
    public void An_unpaid_out_patient_test_waits_for_payment_before_the_sample_is_drawn()
    {
        var progress = LabJourney.Progress(Ordered, Now);

        var current = progress.Current.ShouldNotBeNull();
        current.Key.ShouldBe("ordered");
        current.Detail.ShouldBe("1 test");
        current.MinutesInStage.ShouldBe(60);
        Stage(progress, "payment-cleared").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("1 of 1 test unpaid"));
    }

    [Fact]
    public void A_paid_test_waits_for_collection_from_the_time_it_was_paid()
    {
        var paid = Ordered with { Tests = [Crp with { Paid = true, PaidAt = At(10, 20) }] };

        var current = LabJourney.Progress(paid, Now).Current.ShouldNotBeNull();

        current.Key.ShouldBe("payment-cleared");
        current.MinutesInStage.ShouldBe(40);
    }

    [Fact]
    public void An_in_patient_test_is_billed_to_the_stay_and_does_not_wait_for_payment()
    {
        var progress = LabJourney.Progress(Ordered with { PaysBeforeCollection = false }, Now);

        State(progress, "payment-cleared").ShouldBe(StageState.NotApplicable);
        State(progress, "collected").ShouldBe(StageState.Pending);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("ordered");
    }

    [Fact]
    public void Health_checkup_package_tests_are_paid_with_the_package()
    {
        var package = Ordered with { Tests = [Crp with { CoveredByPackage = true }] };

        State(LabJourney.Progress(package, Now), "payment-cleared").ShouldBe(StageState.NotApplicable);
    }

    [Fact]
    public void A_sample_drawn_without_payment_where_the_hospital_allows_it_skips_the_payment()
    {
        var drawn = Ordered with { Status = LabOrderStatus.InProgress, Tests = [Crp with { CollectedAt = At(10, 15) }] };

        var progress = LabJourney.Progress(drawn, Now);

        State(progress, "payment-cleared").ShouldBe(StageState.Skipped);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("collected");
    }

    [Fact]
    public void A_culture_still_growing_keeps_the_accession_at_resulted_while_the_cbc_is_released()
    {
        var cbc = Crp with
        {
            Paid = true,
            PaidAt = At(8, 0),
            CollectedAt = At(8, 10),
            ReceivedAt = At(8, 30),
            ResultedAt = At(9, 0),
            VerifiedAt = At(9, 10),
            ReleasedAt = At(9, 15),
        };
        var culture = cbc with { ResultedAt = null, VerifiedAt = null, ReleasedAt = null };
        var accession = Ordered with { Status = LabOrderStatus.InProgress, OrderedAt = At(7, 45), Tests = [cbc, culture] };

        var progress = LabJourney.Progress(accession, Now);

        progress.Current.ShouldNotBeNull().Key.ShouldBe("received");
        Stage(progress, "resulted").Detail.ShouldBe("1 of 2 resulted");
        Stage(progress, "released").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Pending), s => s.Detail.ShouldBe("1 of 2 released"));
    }

    [Fact]
    public void Results_verified_by_the_auto_validation_rules_say_so()
    {
        var auto = Ordered with
        {
            Status = LabOrderStatus.InProgress,
            Tests = [Crp with { Paid = true, PaidAt = At(10, 5), CollectedAt = At(10, 10), ReceivedAt = At(10, 20), ResultedAt = At(10, 30), VerifiedAt = At(10, 30), AutoValidated = true }],
        };

        Stage(LabJourney.Progress(auto, Now), "verified").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Current),
            s => s.Detail.ShouldBe("Auto-validated"));
    }

    [Fact]
    public void A_released_report_waits_for_the_ordering_doctors_review()
    {
        var progress = LabJourney.Progress(Released(reviewedAt: null), Now);

        progress.Status.ShouldBe(JourneyStatus.Active);
        progress.Current.ShouldNotBeNull().Key.ShouldBe("released");
        State(progress, "reviewed").ShouldBe(StageState.Pending);
    }

    [Fact]
    public void The_accession_is_finished_once_the_doctor_has_reviewed_the_report()
    {
        var progress = LabJourney.Progress(Released(reviewedAt: At(10, 55)), Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        progress.Current.ShouldBeNull();
        Stage(progress, "reviewed").At.ShouldBe(At(10, 55));
    }

    [Fact]
    public void A_walk_in_order_has_no_doctor_here_to_review_it_and_is_finished_at_release()
    {
        var walkIn = Released(reviewedAt: null) with { HasOrderingDoctor = false };

        var progress = LabJourney.Progress(walkIn, Now);

        progress.Status.ShouldBe(JourneyStatus.Completed);
        Stage(progress, "reviewed").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.NotApplicable),
            s => s.Detail.ShouldBe("No ordering doctor in the hospital"));
    }

    [Fact]
    public void A_cancelled_order_stops_where_it_was()
    {
        var progress = LabJourney.Progress(Ordered with { Status = LabOrderStatus.Cancelled }, Now);

        progress.Status.ShouldBe(JourneyStatus.Cancelled);
        State(progress, "ordered").ShouldBe(StageState.Done);
        progress.Stages.Skip(1).ShouldAllBe(s => s.State == StageState.Stopped);
    }

    private static LabFacts Released(DateTimeOffset? reviewedAt) => Ordered with
    {
        Status = LabOrderStatus.Completed,
        Tests = [Crp with { Paid = true, PaidAt = At(10, 5), CollectedAt = At(10, 10), ReceivedAt = At(10, 15), ResultedAt = At(10, 25), VerifiedAt = At(10, 35), ReleasedAt = At(10, 40) }],
        ReviewedAt = reviewedAt,
    };

    private static JourneyStage Stage(JourneyProgress progress, string key) => progress.Stages.Single(s => s.Key == key);

    private static StageState State(JourneyProgress progress, string key) => Stage(progress, key).State;
}
