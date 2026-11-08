using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// How a stay that ended other than routinely is closed (maker-checker, slice 8): Expired with the patient's death
/// certificate a second doctor confirmed (or, for a medico-legal death whose certificate waits for the post-mortem, a
/// second doctor's approval); Absconded with who searched and, for a medico-legal stay, the police told; LAMA and DAMA with
/// the form's signatory, the doctor and a witness.
/// </summary>
public sealed class DischargeDeclarationRuleTests
{
    private static readonly Guid Stay = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static DeathCertificateStanding Certificate(Guid? stay = null, bool required = true, DateTimeOffset? confirmedAt = null, bool reported = false,
        DateTimeOffset? waiting = null) =>
        new(Guid.NewGuid(), "D-0042", stay, Now.AddHours(-3), false, "Dr Rao", required, confirmedAt is null ? null : "Dr Iyer", confirmedAt, waiting, reported);

    [Fact]
    public void A_confirmed_certificate_closes_the_stay_at_once()
    {
        ExpiredDischarge.Check(Certificate(Stay, confirmedAt: Now), Stay, medicoLegal: false, pendingPostMortem: false).Value.ShouldBe(ExpiredDischargeOutcome.Certified);
        ExpiredDischarge.Check(Certificate(null, confirmedAt: Now), Stay, false, false).Value.ShouldBe(ExpiredDischargeOutcome.Certified);
        ExpiredDischarge.Check(Certificate(Stay, reported: true), Stay, false, false).Value.ShouldBe(ExpiredDischargeOutcome.Certified);
        ExpiredDischarge.Check(Certificate(Stay, required: false), Stay, false, false).Value.ShouldBe(ExpiredDischargeOutcome.Certified);
    }

    [Fact]
    public void A_certificate_not_yet_confirmed_or_none_at_all_holds_the_stay()
    {
        var waiting = ExpiredDischarge.Check(Certificate(Stay, waiting: Now.AddHours(-1)), Stay, false, false);
        (waiting.IsFailure, waiting.Error!.Code).ShouldBe((true, "discharge.death_certificate_unconfirmed"));
        waiting.Error.Message.ShouldContain("D-0042");
        ExpiredDischarge.Check(null, Stay, false, false).Error!.Code.ShouldBe("discharge.death_certificate_required");
        ExpiredDischarge.Check(Certificate(Guid.NewGuid(), confirmedAt: Now), Stay, false, false).Error!.Code.ShouldBe("discharge.death_certificate_other_stay");

        // A certificate that exists is what the stay closes with: "pending post-mortem" does not skip its confirmation.
        ExpiredDischarge.Check(Certificate(Stay), Stay, true, true).Error!.Code.ShouldBe("discharge.death_certificate_unconfirmed");
    }

    [Fact]
    public void Only_a_medico_legal_death_is_closed_pending_post_mortem_and_then_only_with_a_second_doctor()
    {
        ExpiredDischarge.Check(null, Stay, medicoLegal: true, pendingPostMortem: true).Value.ShouldBe(ExpiredDischargeOutcome.AskPendingPostMortem);
        ExpiredDischarge.Check(null, Stay, medicoLegal: false, pendingPostMortem: true).Error!.Code.ShouldBe("discharge.postmortem_not_mlc");
    }

    [Theory]
    [InlineData(DischargeType.Absconded, false, true)]
    [InlineData(DischargeType.Expired, true, true)]
    [InlineData(DischargeType.Expired, false, false)]
    [InlineData(DischargeType.Lama, false, false)]
    [InlineData(DischargeType.Dama, false, false)]
    [InlineData(DischargeType.Normal, false, false)]
    public void Absconded_and_a_death_without_its_certificate_wait_for_a_second_person(DischargeType type, bool pendingPostMortem, bool waits) =>
        DischargeRules.WaitsForSecondPerson(type, pendingPostMortem).ShouldBe(waits);

    [Theory]
    [InlineData(DischargeType.Lama, true)]
    [InlineData(DischargeType.Dama, true)]
    [InlineData(DischargeType.Absconded, false)]
    [InlineData(DischargeType.Normal, false)]
    public void Lama_and_dama_go_with_the_signed_form_and_a_witness(DischargeType type, bool againstAdvice) =>
        DischargeRules.IsAgainstAdvice(type).ShouldBe(againstAdvice);

    [Fact]
    public void An_absconded_report_says_who_searched_and_a_medico_legal_one_that_the_police_were_told()
    {
        var report = new AbscondedReport(Now.AddMinutes(-40), "Ward staff and security", false, null, null, true, "Son, by phone");
        report.Check(medicoLegal: false, Now).ShouldBeNull();
        report.Check(medicoLegal: true, Now)!.Code.ShouldBe("discharge.police_intimation_required");
        (report with { PoliceInformed = true, PoliceStation = "Adyar PS" }).Check(true, Now)!.Code.ShouldBe("discharge.police_intimation_required");
        (report with { PoliceInformed = true, PoliceStation = "Adyar PS", PoliceReference = "GD 114/26" }).Check(true, Now).ShouldBeNull();
        (report with { SearchedBy = " " }).Check(false, Now)!.Code.ShouldBe("validation");
        (report with { PoliceInformed = true }).Check(false, Now)!.Code.ShouldBe("validation");
        (report with { RelativeInformed = null }).Check(false, Now)!.Code.ShouldBe("validation");
        (report with { LastSeenAt = Now.AddHours(1) }).Check(false, Now)!.Code.ShouldBe("validation");
    }
}
