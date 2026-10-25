using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>What a birth report and a medical certificate of cause of death need, and that a reported entry stays.</summary>
public sealed class VitalRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Fact]
    public void A_still_birth_is_of_at_least_28_weeks_and_names_the_cause_of_the_foetal_death()
    {
        var still = Birth() with { Outcome = BirthOutcome.StillBirth, WeeksOfGestation = 24, ChildName = "Baby" };

        new BirthRecord().Record(still, Now, null).Error!.Fields!.Keys.ShouldBe(["weeksOfGestation"]);
        new BirthRecord().Record(still with { WeeksOfGestation = 32 }, Now, null).Error!.Fields!.Keys.ShouldBe(["stillBirthCause"]);

        var birth = new BirthRecord();
        birth.Record(still with { WeeksOfGestation = 32, StillBirthCause = " Abruptio placentae " }, Now, null).IsSuccess.ShouldBeTrue();
        (birth.StillBirthCause, birth.ChildName).ShouldBe(("Abruptio placentae", (string?)null), "a still-born child is not named on Form 3");
    }

    [Fact]
    public void A_birth_is_not_in_the_future_nor_of_an_impossible_weight()
    {
        new BirthRecord().Record(Birth() with { BornAt = Now.AddHours(1) }, Now, null).Error!.Fields!.Keys.ShouldBe(["bornAt"]);
        new BirthRecord().Record(Birth() with { WeightGrams = 32000 }, Now, null).Error!.Fields!.Keys.ShouldBe(["weightGrams"]);
        new BirthRecord().Record(Birth() with { InformantName = " " }, Now, null).Error!.Fields!.Keys.ShouldBe(["informantName"]);
    }

    [Fact]
    public void A_reported_birth_is_corrected_only_through_the_registrar()
    {
        var birth = new BirthRecord();
        birth.Record(Birth(), Now, null).IsSuccess.ShouldBeTrue();

        birth.Report(new RegistrarReport(Today.AddDays(1), "B-2026-118"), Today).Error!.Fields!.Keys.ShouldBe(["reportedOn"]);
        birth.Report(new RegistrarReport(Today, " B-2026-118 "), Today).IsSuccess.ShouldBeTrue();
        (birth.Status, birth.RegistrationNumber).ShouldBe((VitalReportStatus.Reported, "B-2026-118"));

        birth.Record(Birth(), Now, null).Error!.Code.ShouldBe("vital.reported");
        birth.MarkEnteredInError("Wrong mother").Error!.Code.ShouldBe("vital.reported");
    }

    [Fact]
    public void The_certificate_gives_the_causes_in_order_without_gaps_and_asks_a_woman_of_reproductive_age_about_pregnancy()
    {
        new DeathRecord().Certify(Death() with { Causes = [] }, false, null, Now, null).Error!.Fields!.Keys.ShouldBe(["causes"]);
        new DeathRecord().Certify(Death() with { Causes = [new("Septic shock", null), new(" ", null), new("Pneumonia", "5 days")] }, false, null, Now, null)
            .Error!.Fields!.Keys.ShouldBe(["causes"]);
        new DeathRecord().Certify(Death(), true, null, Now, null).Error!.Fields!.Keys.ShouldBe(["pregnancy"]);
        new DeathRecord().Certify(Death() with { Pregnancy = PregnancyAtDeath.NotPregnant }, false, null, Now, null)
            .Error!.Fields!.Keys.ShouldBe(["pregnancy"]);

        var death = new DeathRecord();
        death.Certify(Death() with { Pregnancy = PregnancyAtDeath.WithinSixWeeksOfDelivery }, true, null, Now, null).IsSuccess.ShouldBeTrue();
        death.UnderlyingCause.ShouldBe("Pneumonia", "the last line of Part I is the underlying cause");
    }

    [Fact]
    public void Only_a_medico_legal_case_waits_for_the_investigation_and_a_death_is_after_the_birth_and_before_now()
    {
        new DeathRecord().Certify(Death() with { Manner = DeathManner.PendingInvestigation }, false, null, Now, null).Error!.Fields!.Keys.ShouldBe(["manner"]);
        new DeathRecord().Certify(Death() with { Manner = DeathManner.PendingInvestigation, MedicoLegal = true }, false, null, Now, null).IsSuccess.ShouldBeTrue();
        new DeathRecord().Certify(Death() with { DiedAt = Now.AddMinutes(5) }, false, null, Now, null).Error!.Fields!.Keys.ShouldBe(["diedAt"]);
        new DeathRecord().Certify(Death(), false, Now.AddDays(1), Now.AddDays(2), null).Error!.Fields!.Keys.ShouldBe(["diedAt"]);
    }

    [Fact]
    public void An_entry_made_in_error_leaves_the_register_and_cannot_be_reported()
    {
        var death = new DeathRecord();
        death.Certify(Death(), false, null, Now, null).IsSuccess.ShouldBeTrue();

        death.MarkEnteredInError(" Certified on the wrong patient ").IsSuccess.ShouldBeTrue();
        (death.Status, death.ErrorReason).ShouldBe((VitalReportStatus.EnteredInError, "Certified on the wrong patient"));
        death.Report(new RegistrarReport(Today, "D-1"), Today).Error!.Code.ShouldBe("vital.entered_in_error");
        VitalRegistration.DueBy(Today).ShouldBe(Today.AddDays(21));
    }

    private static BirthDetails Birth() => new(
        Now.AddHours(-3), BirthOutcome.LiveBirth, BirthSex.Female, 3100, 39, DeliveryMethod.Natural, BirthAttendant.Doctor, null, "Ravi Kumar", 27, 2,
        "Ward sister, labour room", null);

    private static DeathCertificate Death() => new(
        Now.AddHours(-2), [new("Septic shock", "6 hours"), new("Pneumonia", "5 days")], "Type 2 diabetes", DeathManner.Natural, false,
        PregnancyAtDeath.NotApplicable, "Dr. Meera", "TNMC 45120");
}
