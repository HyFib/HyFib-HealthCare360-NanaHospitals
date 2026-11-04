using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>
/// The bedside check before a unit is started: this patient, this unit, still usable, with consent; and the
/// transfusion's observations and end.
/// </summary>
public sealed class BedsideCheckTests
{
    private static readonly DateTimeOffset Issued = TestData.Now;

    [Fact]
    public void The_wristband_and_the_bag_are_compared_with_the_issue_as_read_whatever_their_case_and_spaces()
    {
        BedsideCheck.Identity("NH-000123", " nh-000123 ", "NH26000123-R", "nh26000123-r").IsSuccess.ShouldBeTrue();

        BedsideCheck.Identity("NH-000123", "NH-000124", "NH26000123-R", "NH26000123-R").Error!.Code.ShouldBe("bloodbank.wrong_patient");
        BedsideCheck.Identity("NH-000123", "NH-000123", "NH26000123-R", "NH26000124-R").Error!.Code.ShouldBe("bloodbank.wrong_unit");
    }

    [Fact]
    public void A_unit_is_usable_in_date_with_a_standing_cross_match_and_within_four_hours_of_leaving_the_bank()
    {
        var crossMatch = new CrossMatch { Result = CrossmatchResult.Compatible, PerformedAt = Issued.AddHours(-2) };
        var expires = Issued.AddDays(10);

        BedsideCheck.Usable(Issued.AddMinutes(20), Issued, expires, emergencyRelease: false, crossMatch).IsSuccess.ShouldBeTrue();
        BedsideCheck.Usable(Issued.AddHours(4), Issued, expires, emergencyRelease: false, crossMatch).IsSuccess.ShouldBeTrue();

        BedsideCheck.Usable(Issued.AddHours(4).AddMinutes(1), Issued, expires, emergencyRelease: false, crossMatch).Error!.Code
            .ShouldBe("bloodbank.out_of_storage_too_long");
        BedsideCheck.Usable(Issued.AddMinutes(20), Issued, Issued.AddMinutes(10), emergencyRelease: false, crossMatch).Error!.Code
            .ShouldBe("bloodbank.unit_expired");
    }

    [Fact]
    public void A_cross_match_that_no_longer_stands_refuses_the_start_unless_the_unit_was_released_in_an_emergency()
    {
        var old = new CrossMatch { Result = CrossmatchResult.Compatible, PerformedAt = Issued.AddHours(-71) };
        var at = Issued.AddHours(2);

        BedsideCheck.Usable(at, Issued, Issued.AddDays(10), emergencyRelease: false, old).Error!.Code.ShouldBe("bloodbank.crossmatch_expired");
        BedsideCheck.Usable(at, Issued, Issued.AddDays(10), emergencyRelease: false, crossMatch: null).Error!.Code.ShouldBe("bloodbank.crossmatch_expired");
        BedsideCheck.Usable(at, Issued, Issued.AddDays(10), emergencyRelease: true, crossMatch: null).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Without_consent_only_a_unit_released_in_an_emergency_starts_and_only_with_the_reason()
    {
        BedsideCheck.Consent(consented: true, emergencyRelease: false, deferredReason: null).IsSuccess.ShouldBeTrue();
        BedsideCheck.Consent(consented: false, emergencyRelease: false, deferredReason: "Unconscious").Error!.Code.ShouldBe("bloodbank.consent_required");
        BedsideCheck.Consent(consented: false, emergencyRelease: true, deferredReason: "  ").Error!.Code.ShouldBe("bloodbank.consent_required");
        BedsideCheck.Consent(consented: false, emergencyRelease: true, deferredReason: "Unconscious, no relative present").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_consent_counts_from_the_admission_or_else_from_the_day_before_the_blood_was_asked_for()
    {
        var admitted = Issued.AddDays(-3);

        BedsideCheck.ConsentSince(Issued.AddHours(-1), admitted).ShouldBe(admitted);
        BedsideCheck.ConsentSince(Issued, admittedAt: null).ShouldBe(Issued.AddHours(-24));
    }

    [Fact]
    public void Observations_are_taken_after_the_start_while_it_runs_and_the_end_closes_it()
    {
        var transfusion = Running();

        transfusion.Observe(Observation(TransfusionObservationKind.FifteenMinutes, Issued.AddMinutes(30))).IsSuccess.ShouldBeTrue();
        transfusion.Observe(Observation(TransfusionObservationKind.Later, Issued.AddMinutes(-5))).Error!.Fields!.Keys.ShouldContain("recordedAt");
        transfusion.Observe(Observation(TransfusionObservationKind.End, Issued.AddMinutes(40))).Error!.Fields!.Keys.ShouldContain("kind");

        transfusion.End(Issued.AddHours(2), stopped: false, stopReason: null, volumeMl: 280, unitVolumeMl: 280, Observation(TransfusionObservationKind.Later, Issued.AddHours(2)),
            userId: null, userName: "Sister Rani").IsSuccess.ShouldBeTrue();

        (transfusion.Status, transfusion.VolumeMl, transfusion.StopReason).ShouldBe((TransfusionStatus.Completed, 280, (string?)null));
        transfusion.Observations.Last().Kind.ShouldBe(TransfusionObservationKind.End);
        transfusion.Observe(Observation(TransfusionObservationKind.Later, Issued.AddHours(3))).Error!.Code.ShouldBe("bloodbank.transfusion_ended");
        transfusion.End(Issued.AddHours(3), stopped: false, stopReason: null, volumeMl: 0, unitVolumeMl: 280, Observation(TransfusionObservationKind.End, Issued.AddHours(3)),
            userId: null, userName: "Sister Rani").Error!.Code.ShouldBe("bloodbank.transfusion_ended");
    }

    [Fact]
    public void A_stop_says_why_and_the_volume_given_is_no_more_than_the_bag_held()
    {
        var transfusion = Running();
        var end = Observation(TransfusionObservationKind.End, Issued.AddMinutes(20));

        transfusion.End(Issued.AddMinutes(20), stopped: true, stopReason: " ", volumeMl: 50, unitVolumeMl: 280, end, null, "Sister Rani").Error!.Fields!.Keys
            .ShouldContain("stopReason");
        transfusion.End(Issued.AddMinutes(20), stopped: false, stopReason: null, volumeMl: 330, unitVolumeMl: 280, end, null, "Sister Rani").Error!.Fields!.Keys
            .ShouldContain("volumeMl");
        transfusion.End(Issued.AddMinutes(-1), stopped: false, stopReason: null, volumeMl: 0, unitVolumeMl: 280, end, null, "Sister Rani").Error!.Fields!.Keys
            .ShouldContain("endedAt");

        transfusion.End(Issued.AddMinutes(20), stopped: true, stopReason: " Rigors and fever ", volumeMl: 308, unitVolumeMl: 280, end, null, "Sister Rani")
            .IsSuccess.ShouldBeTrue("a bag holds a little more than its label says");
        (transfusion.Status, transfusion.StopReason).ShouldBe((TransfusionStatus.Stopped, "Rigors and fever"));
    }

    private static Transfusion Running() => new() { StartedAt = Issued, Status = TransfusionStatus.InProgress };

    private static TransfusionObservation Observation(TransfusionObservationKind kind, DateTimeOffset at) => new()
    {
        Kind = kind,
        RecordedAt = at,
        TemperatureC = 36.8m,
        PulsePerMinute = 82,
        SystolicMmHg = 118,
        DiastolicMmHg = 76,
        RespiratoryRate = 16,
    };
}
