using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// The Emergency Severity Index (v4) decision points, the danger-zone vitals by age, the triage nurse's override, the
/// ER case's disposition rules and the MLC register's rules, with a sexual-assault victim's examination (BNSS s.184) and a
/// child's (POCSO s.19 and s.27).
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
        mlc.Describe(MlcBroughtBy.Police, null, MlcCause.Assault, null, null, "Lacerated wound", null, null, "Adyar", false, Now).Error!.Fields!.Keys.ShouldContain("broughtByName");
        mlc.Describe(MlcBroughtBy.Relative, " ", MlcCause.Assault, null, null, "Lacerated wound", null, null, "Adyar", false, Now).Error!.Fields!.Keys.ShouldContain("broughtByName");
        Describe(mlc).IsSuccess.ShouldBeTrue();
        mlc.IntimationOverdue(Now).ShouldBeTrue("An hour has passed without informing the police.");

        mlc.Intimate(Now.AddMinutes(6), MlcIntimationMode.Phone, "HC 1234 Ravi", null, null, Now).Error!.Fields!.Keys.ShouldContain("intimatedAt");
        mlc.Intimate(Now.AddMinutes(-10), MlcIntimationMode.Phone, " ", null, null, Now).Error!.Fields!.Keys.ShouldContain("receivedBy");
        mlc.Intimate(Now.AddMinutes(-10), MlcIntimationMode.Phone, "HC 1234 Ravi", "GD 45/2026", null, Now).IsSuccess.ShouldBeTrue();

        mlc.IntimationOverdue(Now).ShouldBeFalse();
        Describe(mlc).Error!.Code.ShouldBe("mlc.intimated");
        mlc.Intimate(Now, MlcIntimationMode.Written, "SI Kumar", null, null, Now).Error!.Code.ShouldBe("mlc.intimated");
    }

    [Fact]
    public void A_good_samaritan_need_not_give_their_name()
    {
        var mlc = new MlcRecord { IdentifiedAt = Now.AddMinutes(-30) };
        mlc.Describe(MlcBroughtBy.Public, null, MlcCause.RoadTrafficAccident, Now.AddHours(-1), "OMR near Thoraipakkam", "Abrasion, right knee, simple",
            true, false, "Adyar", false, Now).IsSuccess.ShouldBeTrue();
        (mlc.BroughtBy, mlc.BroughtByName).ShouldBe((MlcBroughtBy.Public, (string?)null));
    }

    [Fact]
    public void A_sexual_offence_against_a_child_is_reported_to_the_juvenile_police_unit_or_the_local_police()
    {
        var child = SexualAssault(isChild: true);
        child.IsPocso.ShouldBeTrue();
        child.Intimate(Now.AddMinutes(-10), MlcIntimationMode.Phone, "SI Meena", "GD 12/2026", null, Now).Error!.Fields!.Keys.ShouldContain("reportedTo");
        child.Intimate(Now.AddMinutes(-10), MlcIntimationMode.Phone, "SI Meena", "GD 12/2026", PocsoReportedTo.SpecialJuvenilePoliceUnit, Now).IsSuccess
            .ShouldBeTrue();
        child.ReportedTo.ShouldBe(PocsoReportedTo.SpecialJuvenilePoliceUnit);

        // An adult's case is reported to the police station as any other.
        var adult = SexualAssault(isChild: false);
        adult.IsPocso.ShouldBeFalse();
        adult.Intimate(Now.AddMinutes(-10), MlcIntimationMode.Phone, "SI Meena", null, PocsoReportedTo.LocalPolice, Now).IsSuccess.ShouldBeTrue();
        adult.ReportedTo.ShouldBeNull();
    }

    [Fact]
    public void A_girl_child_is_examined_by_a_woman_doctor_with_her_parent_or_a_trusted_person_present()
    {
        var mlc = SexualAssault(isChild: true);

        mlc.RecordExamination(Examination(Gender.Male), Gender.Female, Now).Error!.Code.ShouldBe("pocso.woman_doctor");
        mlc.RecordExamination(Examination(Gender.Female) with { AttendantKind = null }, Gender.Female, Now).Error!.Fields!.Keys.ShouldContain("attendantName");
        mlc.RecordExamination(Examination(Gender.Female) with { AttendantName = " " }, Gender.Female, Now).Error!.Fields!.Keys.ShouldContain("attendantName");
        mlc.RecordExamination(Examination(Gender.Female), Gender.Female, Now).IsSuccess.ShouldBeTrue();
        (mlc.ExaminerName, mlc.AttendantKind, mlc.AttendantName).ShouldBe(("Dr. Lakshmi Iyer", ExaminationAttendant.Parent, "Mother, Selvi"));

        // A boy may be examined by a man; who was present is named for any child.
        var boy = SexualAssault(isChild: true);
        boy.RecordExamination(Examination(Gender.Male), Gender.Male, Now).IsSuccess.ShouldBeTrue();
        boy.RecordExamination(Examination(Gender.Male) with { AttendantKind = ExaminationAttendant.WomanNominatedByHead, AttendantName = "Sr. Mary, nursing superintendent" },
            Gender.Male, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_victims_examination_keeps_its_exact_times_and_who_consented_and_its_report_is_due_in_seven_days()
    {
        var adult = SexualAssault(isChild: false);
        adult.RecordExamination(Examination(Gender.Male) with { ConsentGivenBy = " " }, Gender.Female, Now).Error!.Fields!.Keys.ShouldContain("consentGivenBy");
        adult.RecordExamination(Examination(Gender.Male) with { EndedAt = Now.AddMinutes(-90) }, Gender.Female, Now).Error!.Fields!.Keys.ShouldContain("endedAt");
        adult.RecordExamination(Examination(Gender.Male) with { EndedAt = Now.AddMinutes(6) }, Gender.Female, Now).Error!.Fields!.Keys.ShouldContain("endedAt");
        adult.RecordExamination(Examination(Gender.Male) with { StartedAt = Now.AddHours(-4) }, Gender.Female, Now).Error!.Fields!.Keys.ShouldContain("startedAt");

        // An adult's examination by a man, with her consent, and nobody named present: BNSS s.184 asks no more.
        adult.RecordExamination(Examination(Gender.Male) with { AttendantKind = null, AttendantName = null }, Gender.Female, Now).IsSuccess.ShouldBeTrue();
        (adult.ExaminationStartedAt, adult.ExaminationEndedAt, adult.AttendantName).ShouldBe((Now.AddMinutes(-60), Now.AddMinutes(-20), (string?)null));
        adult.ReportDueAt.ShouldBe(Now.AddMinutes(-20).AddDays(7));
        adult.ReportOverdue(Now.AddDays(7).AddMinutes(-21)).ShouldBeFalse();
        adult.ReportOverdue(Now.AddDays(7).AddMinutes(-19)).ShouldBeTrue("Seven days have passed since the examination ended.");

        adult.ForwardReport(Now.AddMinutes(-30), "SI Meena, Adyar", Now).Error!.Fields!.Keys.ShouldContain("forwardedAt");
        adult.ForwardReport(Now, " ", Now).Error!.Fields!.Keys.ShouldContain("investigatingOfficer");
        adult.ForwardReport(Now, "SI Meena, Adyar", Now).IsSuccess.ShouldBeTrue();
        adult.ReportOverdue(Now.AddDays(30)).ShouldBeFalse();
        adult.ForwardReport(Now, "SI Meena, Adyar", Now).Error!.Code.ShouldBe("mlc.report_forwarded");
        adult.RecordExamination(Examination(Gender.Male), Gender.Female, Now).Error!.Code.ShouldBe("mlc.report_forwarded");

        // Only a sexual assault has this examination; and its report is forwarded once it is recorded.
        var assault = new MlcRecord { IdentifiedAt = Now.AddMinutes(-30) };
        Describe(assault).IsSuccess.ShouldBeTrue();
        assault.RecordExamination(Examination(Gender.Male), Gender.Female, Now).Error!.Code.ShouldBe("mlc.not_sexual_assault");
        SexualAssault(isChild: false).ForwardReport(Now, "SI Meena, Adyar", Now).Error!.Code.ShouldBe("mlc.no_examination");
    }

    [Fact]
    public void A_case_found_to_be_a_childs_after_its_examination_shows_what_of_s27_the_examination_lacks()
    {
        // Entered as an adult's and examined by a man with nobody present, as the law allows for an adult.
        var sent = AdultExaminedByAMan();
        sent.MissingSafeguards(Gender.Female).ShouldBeEmpty();

        // Found to be a child's: the correction stands, and the examination stays as it was done, showing what it lacks.
        BecomesAChilds(sent).IsSuccess.ShouldBeTrue();
        (sent.IsPocso, sent.ExaminerGender).ShouldBe((true, (Gender?)Gender.Male));
        sent.MissingSafeguards(Gender.Female).ShouldBe([PocsoSafeguard.WomanDoctor, PocsoSafeguard.PersonPresent]);
        sent.MissingSafeguards(Gender.Male).ShouldBe([PocsoSafeguard.PersonPresent]);

        // Its report goes to the investigating officer all the same (BNSS s.184(6)), and still shows it.
        sent.ForwardReport(Now, "SI Meena, AWPS Velachery", Now).IsSuccess.ShouldBeTrue();
        sent.MissingSafeguards(Gender.Female).Count.ShouldBe(2);

        // Recorded wrongly, it is corrected as a child's examination is recorded, and then lacks nothing.
        var corrected = AdultExaminedByAMan();
        BecomesAChilds(corrected).IsSuccess.ShouldBeTrue();
        corrected.RecordExamination(Examination(Gender.Female), Gender.Female, Now).IsSuccess.ShouldBeTrue();
        corrected.MissingSafeguards(Gender.Female).ShouldBeEmpty();
    }

    [Fact]
    public void A_time_a_desk_sends_as_now_is_taken_though_its_clock_runs_a_few_minutes_ahead()
    {
        // "Police informed now" and "Report sent now" send the desk's clock; more than five minutes ahead is the future.
        var mlc = SexualAssault(isChild: false);
        mlc.Intimate(Now.AddMinutes(2), MlcIntimationMode.Phone, "SI Meena", null, null, Now).IsSuccess.ShouldBeTrue();
        mlc.RecordExamination(Examination(Gender.Male) with { EndedAt = Now.AddMinutes(2) }, Gender.Female, Now).IsSuccess.ShouldBeTrue();
        mlc.ForwardReport(Now.AddMinutes(6), "SI Meena, Adyar", Now).Error!.Fields!.Keys.ShouldContain("forwardedAt");
        mlc.ForwardReport(Now.AddMinutes(3), "SI Meena, Adyar", Now).IsSuccess.ShouldBeTrue();
    }

    private static Result Describe(MlcRecord mlc, string injuries = "Lacerated wound 3 x 1 cm, left forearm, simple", string policeStation = "Adyar") =>
        mlc.Describe(MlcBroughtBy.Relative, "Brother, Kumar", MlcCause.RoadTrafficAccident, Now.AddHours(-2), "OMR near Thoraipakkam", injuries, true, false,
            policeStation, false, Now);

    private static MlcRecord SexualAssault(bool isChild)
    {
        var mlc = new MlcRecord { IdentifiedAt = Now.AddMinutes(-30) };
        mlc.Describe(MlcBroughtBy.Relative, "Mother, Selvi", MlcCause.SexualAssault, Now.AddHours(-3), "Near the school, Velachery", "Abrasions, both forearms",
            true, false, "Velachery", isChild, Now).IsSuccess.ShouldBeTrue();
        return mlc;
    }

    private static MlcRecord AdultExaminedByAMan()
    {
        var mlc = SexualAssault(isChild: false);
        mlc.RecordExamination(Examination(Gender.Male) with { AttendantKind = null, AttendantName = null }, Gender.Female, Now).IsSuccess.ShouldBeTrue();
        return mlc;
    }

    private static Result BecomesAChilds(MlcRecord mlc) => mlc.Describe(
        MlcBroughtBy.Relative, "Mother, Selvi", MlcCause.SexualAssault, Now.AddHours(-3), "Near the school, Velachery", "Abrasions, both forearms", true, false,
        "Velachery", true, Now);

    private static SexualAssaultExamination Examination(Gender examinerGender) => new(
        Guid.NewGuid(), examinerGender == Gender.Female ? "Dr. Lakshmi Iyer" : "Dr. Ravi Kumar", "TNMC 45678", examinerGender, Now.AddMinutes(-60),
        Now.AddMinutes(-20), "The patient's mother, Selvi", ExaminationAttendant.Parent, "Mother, Selvi");
}
