using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Mtp;

/// <summary>When the MTP Act allows a termination on what was recorded, whose consent it needs, and that a recorded one stays.</summary>
public sealed class MtpCaseTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 5, 30, 0, TimeSpan.Zero);
    private static readonly MtpOpinion First = new("Dr. Meera", "TNMC 45120", Today);
    private static readonly MtpOpinion Second = new("Dr. Lata", "TNMC 51877", Today);

    [Theory]
    [InlineData(8, 1, true)]
    [InlineData(20, 1, true)]
    [InlineData(21, 1, false)]
    [InlineData(21, 2, true)]
    [InlineData(24, 2, true)]
    public void The_length_of_the_pregnancy_decides_how_many_doctors_form_the_opinion(int weeks, int doctors, bool allowed)
    {
        var opinions = new[] { First, Second }.Take(doctors).ToList();
        var result = new MtpCase().Certify(
            Certification() with { WeeksOfGestation = weeks, Opinions = opinions, Category = weeks > 20 ? MtpRule3BCategory.ChangeOfMaritalStatus : null },
            Today, null, Now);

        result.IsSuccess.ShouldBe(allowed, result.Error?.Message);
        if (!allowed)
        {
            result.Error!.Fields!.Keys.ShouldBe(["opinions"]);
        }
    }

    [Fact]
    public void From_20_to_24_weeks_the_woman_is_in_a_rule_3b_category_and_a_contraceptive_failure_is_no_ground()
    {
        var late = Certification() with { WeeksOfGestation = 22, Opinions = [First, Second] };

        new MtpCase().Certify(late, Today, null, Now).Error!.Fields!.Keys.ShouldBe(["category"]);
        new MtpCase().Certify(late with { Category = MtpRule3BCategory.Minor, Grounds = [MtpGround.ContraceptiveFailure] }, Today, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["grounds"]);
        new MtpCase().Certify(late with { Opinions = [First, First with { DoctorName = "Dr. Meera R" }], Category = MtpRule3BCategory.Minor }, Today, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["opinions"], "the two opinions are of two doctors");
    }

    [Fact]
    public void Beyond_24_weeks_only_a_foetal_abnormality_the_medical_board_found()
    {
        var board = Certification() with { WeeksOfGestation = 28, Opinions = [First, Second], Grounds = [MtpGround.FoetalAbnormality] };

        new MtpCase().Certify(board, Today, null, Now).Error!.Fields!.Keys.ShouldBe(["medicalBoardReference"]);
        new MtpCase().Certify(board with { Grounds = [MtpGround.InjuryToMentalHealth], MedicalBoardReference = "SMB/2026/14" }, Today, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["grounds"]);

        var mtp = new MtpCase();
        mtp.Certify(board with { MedicalBoardReference = " SMB/2026/14 " }, Today, null, Now).IsSuccess.ShouldBeTrue();
        mtp.MedicalBoardReference.ShouldBe("SMB/2026/14");
    }

    [Fact]
    public void A_minor_or_a_mentally_ill_woman_is_consented_for_by_her_guardian_and_an_adult_by_herself()
    {
        new MtpCase().Certify(Certification() with { AgeYears = 16 }, Today, null, Now).Error!.Fields!.Keys.ShouldBe(["consentBy"]);
        new MtpCase().Certify(Certification() with { MentallyIll = true }, Today, null, Now).Error!.Fields!.Keys.ShouldBe(["consentBy"]);
        new MtpCase().Certify(Certification() with { ConsentBy = MtpConsentBy.Guardian, GuardianName = "Ravi", GuardianRelation = "Husband" }, Today, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["consentBy"], "an adult's husband does not consent for her");
        new MtpCase().Certify(Certification() with { AgeYears = 16, ConsentBy = MtpConsentBy.Guardian }, Today, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["guardianName"]);

        var minor = new MtpCase();
        minor.Certify(Certification() with { AgeYears = 16, ConsentBy = MtpConsentBy.Guardian, GuardianName = " Selvi ", GuardianRelation = "Mother" },
            Today, null, Now).IsSuccess.ShouldBeTrue();
        (minor.GuardianName, minor.GuardianRelation).ShouldBe(("Selvi", "Mother"));
    }

    [Fact]
    public void The_termination_comes_after_the_opinion_and_then_the_case_cannot_change()
    {
        var mtp = new MtpCase();
        mtp.Certify(Certification() with { ConsentOn = Today.AddDays(-2), Opinions = [First with { OpinionOn = Today.AddDays(-1) }] }, Today, null, Now)
            .IsSuccess.ShouldBeTrue();

        mtp.Terminate(Termination() with { TerminatedOn = Today.AddDays(-2) }, Today, Now).Error!.Fields!.Keys.ShouldBe(["terminatedOn"]);
        mtp.Terminate(Termination() with { TerminatedOn = Today.AddDays(1) }, Today, Now).Error!.Fields!.Keys.ShouldBe(["terminatedOn"]);
        mtp.Terminate(Termination() with { DischargedOn = Today.AddDays(-3) }, Today, Now).Error!.Fields!.Keys.ShouldBe(["dischargedOn"]);
        mtp.Terminate(Termination() with { Method = MtpMethod.Other }, Today, Now).Error!.Fields!.Keys.ShouldBe(["remarks"]);

        mtp.Terminate(Termination(), Today, Now).IsSuccess.ShouldBeTrue();
        (mtp.Status, mtp.TerminatedOn, mtp.Method).ShouldBe((MtpCaseStatus.Terminated, (DateOnly?)Today, (MtpMethod?)MtpMethod.Medical));

        mtp.Certify(Certification(), Today, null, Now).Error!.Code.ShouldBe("mtp.terminated");
        mtp.Cancel("Withdrew consent").Error!.Code.ShouldBe("mtp.terminated");
    }

    [Fact]
    public void A_cancelled_case_cannot_be_terminated()
    {
        var mtp = new MtpCase();
        mtp.Certify(Certification(), Today, null, Now).IsSuccess.ShouldBeTrue();
        mtp.Cancel(" Withdrew her consent ").IsSuccess.ShouldBeTrue();

        (mtp.Status, mtp.CancelReason).ShouldBe((MtpCaseStatus.Cancelled, "Withdrew her consent"));
        mtp.Terminate(Termination(), Today, Now).Error!.Code.ShouldBe("mtp.cancelled");
    }

    private static MtpCertification Certification() => new(
        AgeYears: 27, MentallyIll: false, MtpConsentBy.Woman, GuardianName: null, GuardianRelation: null, ConsentOn: Today, ConsentFileId: null,
        WeeksOfGestation: 8, Grounds: [MtpGround.InjuryToMentalHealth], Category: null, MedicalBoardReference: null, Opinions: [First]);

    private static MtpTermination Termination() => new(Today, MtpMethod.Medical, "Dr. Meera", Today, "Complete abortion, discharged well", null);
}
