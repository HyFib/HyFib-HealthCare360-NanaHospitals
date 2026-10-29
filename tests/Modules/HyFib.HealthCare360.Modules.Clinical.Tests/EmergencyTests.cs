using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// The Emergency Severity Index (v4) decision points, the danger-zone vitals by age, the triage nurse's override, the
/// ER case's disposition rules and the MLC register's rules.
/// </summary>
public sealed class EmergencyTests
{
    private const int Adult = 40 * 365;
    private static readonly TriageVitals Normal = new(80, 16, 98, 120, 80, 37.0m, 15, 2);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, false, false, false, 2, 1)]
    [InlineData(false, true, false, false, 0, 2)]
    [InlineData(false, false, true, false, 0, 2)]
    [InlineData(false, false, false, true, 0, 2)]
    [InlineData(false, false, false, false, 2, 3)]
    [InlineData(false, false, false, false, 1, 4)]
    [InlineData(false, false, false, false, 0, 5)]
    public void The_decision_points_give_the_level(bool lifeSaving, bool highRisk, bool confused, bool distress, int resources, int level) =>
        EmergencySeverityIndex.Assess(new EsiAnswers(lifeSaving, highRisk, confused, distress, resources), Normal, Adult).Level.ShouldBe(level);

    [Fact]
    public void Measured_vitals_feed_decision_points_a_and_b()
    {
        var none = new EsiAnswers(false, false, false, false, 0);
        EmergencySeverityIndex.Assess(none, Normal with { Gcs = 7 }, Adult).Level.ShouldBe(1);
        EmergencySeverityIndex.Assess(none, Normal with { Gcs = 13 }, Adult).Reasons.ShouldContain("B: GCS 13, altered mental status.");
        EmergencySeverityIndex.Assess(none, Normal with { PainScore = 8 }, Adult).Level.ShouldBe(2);
        EmergencySeverityIndex.Assess(none, Normal with { TemperatureCelsius = 38.2m }, 40).Level.ShouldBe(2, "A febrile baby under three months is high risk.");
        EmergencySeverityIndex.Assess(none, Normal with { TemperatureCelsius = 38.2m }, Adult).Level.ShouldBe(5);
    }

    [Theory]
    [InlineData(40, 175, 45, false)]
    [InlineData(40, 185, 45, true)]
    [InlineData(400, 165, 30, true)]
    [InlineData(5 * 365, 135, 32, true)]
    [InlineData(Adult, 98, 20, false)]
    [InlineData(Adult, 101, 18, true)]
    [InlineData(null, 101, 18, true)]
    public void Danger_zone_vitals_depend_on_the_age(int? ageInDays, int pulse, int respiration, bool danger) =>
        (EmergencySeverityIndex.DangerZoneReasons(Normal with { Pulse = pulse, RespiratoryRate = respiration }, ageInDays).Count > 0).ShouldBe(danger);

    [Fact]
    public void Many_resources_with_danger_zone_vitals_considers_level_2()
    {
        var many = new EsiAnswers(false, false, false, false, 2);

        var low = EmergencySeverityIndex.Assess(many, Normal with { SpO2 = 90 }, Adult);

        (low.Level, low.DangerZoneVitals).ShouldBe((2, true));
        low.Reasons.ShouldContain("D: SpO2 90% (below 92), in the danger zone; consider level 2.");
        EmergencySeverityIndex.Assess(many, Normal, Adult).Level.ShouldBe(3);
    }

    [Fact]
    public void A_level_other_than_the_algorithms_needs_a_reason()
    {
        var many = new EsiAnswers(false, false, false, false, 2);
        var vitals = Normal with { Pulse = 110 };

        EsiTriage.Assess(Guid.NewGuid(), "Chest pain", many, vitals, Adult, 3, null, "Sr. Asha", Now).Error!.Fields!.Keys.ShouldContain("overrideReason");

        var kept = EsiTriage.Assess(Guid.NewGuid(), "Chest pain", many, vitals, Adult, 3, "Pulse settles at rest; anxious", "Sr. Asha", Now).Value;
        (kept.ComputedLevel, kept.Level, kept.OverrideReason).ShouldBe((2, 3, "Pulse settles at rest; anxious"));
        EsiTriage.Assess(Guid.NewGuid(), "Chest pain", many, vitals, Adult, null, null, "Sr. Asha", Now).Value.Level.ShouldBe(2);
        EsiTriage.Assess(Guid.NewGuid(), " ", many, vitals, Adult, null, null, "Sr. Asha", Now).Error!.Fields!.Keys.ShouldContain("chiefComplaint");
    }

    [Fact]
    public void A_case_is_triaged_before_its_disposition_and_closes_once()
    {
        var c = new EmergencyCase { ArrivedAt = Now.AddMinutes(-30) };
        c.Dispose(EmergencyDisposition.Discharged, null, null, "Dr. Rao", Now).Error!.Code.ShouldBe("er.not_triaged");

        var triage = EsiTriage.Assess(c.Id, "Cut finger", new EsiAnswers(false, false, false, false, 1), Normal, Adult, null, null, "Sr. Asha", Now.AddMinutes(-25)).Value;
        c.Triage(triage, ArrivalMode.WalkIn).IsSuccess.ShouldBeTrue();
        (c.Level, c.TriagedAt).ShouldBe((4, Now.AddMinutes(-25)));
        c.SeenBy("Dr. Rao", Now.AddMinutes(-10));
        c.SeenBy("Dr. Iyer", Now.AddMinutes(-5));
        (c.DoctorName, c.DoctorSeenAt).ShouldBe(("Dr. Rao", Now.AddMinutes(-10)), "The first doctor's time is the door-to-doctor time.");

        c.Dispose(EmergencyDisposition.ReferredOut, null, " ", "Dr. Rao", Now).Error!.Fields!.Keys.ShouldContain("referredTo");
        c.Dispose(EmergencyDisposition.Lama, null, null, "Dr. Rao", Now).Error!.Fields!.Keys.ShouldContain("notes");
        c.Dispose(EmergencyDisposition.Discharged, "Sutured; review in 5 days", null, "Dr. Rao", Now).IsSuccess.ShouldBeTrue();
        c.IsOpen.ShouldBeFalse();
        c.Dispose(EmergencyDisposition.Admitted, null, null, "Dr. Rao", Now).Error!.Code.ShouldBe("er.closed");
    }

    [Fact]
    public void A_patient_who_dies_or_absconds_before_triage_can_still_leave_the_board()
    {
        var c = new EmergencyCase { ArrivedAt = Now.AddMinutes(-5) };
        c.Dispose(EmergencyDisposition.Died, "Brought in without signs of life; declared 10:00", null, "Dr. Rao", Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_mlc_entry_is_fixed_once_the_police_are_informed()
    {
        var mlc = new MlcRecord { IdentifiedAt = Now.AddMinutes(-90) };
        Describe(mlc, policeStation: " ").Error!.Fields!.Keys.ShouldContain("policeStation");
        Describe(mlc, injuries: "").Error!.Fields!.Keys.ShouldContain("injuries");
        mlc.Describe(MlcBroughtBy.Police, null, MlcCause.Assault, null, null, "Lacerated wound", null, null, "Adyar", Now).Error!.Fields!.Keys.ShouldContain("broughtByName");
        mlc.Describe(MlcBroughtBy.Relative, " ", MlcCause.Assault, null, null, "Lacerated wound", null, null, "Adyar", Now).Error!.Fields!.Keys.ShouldContain("broughtByName");
        Describe(mlc).IsSuccess.ShouldBeTrue();
        mlc.IntimationOverdue(Now).ShouldBeTrue("An hour has passed without informing the police.");

        mlc.Intimate(Now.AddMinutes(5), MlcIntimationMode.Phone, "HC 1234 Ravi", null, Now).Error!.Fields!.Keys.ShouldContain("intimatedAt");
        mlc.Intimate(Now.AddMinutes(-10), MlcIntimationMode.Phone, " ", null, Now).Error!.Fields!.Keys.ShouldContain("receivedBy");
        mlc.Intimate(Now.AddMinutes(-10), MlcIntimationMode.Phone, "HC 1234 Ravi", "GD 45/2026", Now).IsSuccess.ShouldBeTrue();

        mlc.IntimationOverdue(Now).ShouldBeFalse();
        Describe(mlc).Error!.Code.ShouldBe("mlc.intimated");
        mlc.Intimate(Now, MlcIntimationMode.Written, "SI Kumar", null, Now).Error!.Code.ShouldBe("mlc.intimated");
    }

    [Fact]
    public void A_good_samaritan_need_not_give_their_name()
    {
        var mlc = new MlcRecord { IdentifiedAt = Now.AddMinutes(-30) };
        mlc.Describe(MlcBroughtBy.Public, null, MlcCause.RoadTrafficAccident, Now.AddHours(-1), "OMR near Thoraipakkam", "Abrasion, right knee, simple",
            true, false, "Adyar", Now).IsSuccess.ShouldBeTrue();
        (mlc.BroughtBy, mlc.BroughtByName).ShouldBe((MlcBroughtBy.Public, (string?)null));
    }

    private static Result Describe(MlcRecord mlc, string injuries = "Lacerated wound 3 x 1 cm, left forearm, simple", string policeStation = "Adyar") =>
        mlc.Describe(MlcBroughtBy.Relative, "Brother, Kumar", MlcCause.RoadTrafficAccident, Now.AddHours(-2), "OMR near Thoraipakkam", injuries, true, false,
            policeStation, Now);
}
