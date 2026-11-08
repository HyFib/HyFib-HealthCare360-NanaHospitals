using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Mtp;

/// <summary>When the MTP Act allows a termination on what was recorded, whose consent it needs, and that a recorded one stays.</summary>
public sealed class MtpCaseTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 5, 30, 0, TimeSpan.Zero);
    private static readonly MtpOpinion First = new("Dr. Meera", "TNMC 45120", Today);
    private static readonly MtpOpinion Second = new("Dr. Lata", "TNMC 51877", Today);

    private static readonly Guid Certifier = Guid.NewGuid();
    private static readonly Guid SecondDoctor = Guid.NewGuid();

    [Theory]
    [InlineData(8, 1, true, false)]
    [InlineData(20, 1, true, false)]
    [InlineData(20, 2, true, false)]
    [InlineData(21, 1, true, true)]
    [InlineData(24, 1, true, true)]
    [InlineData(21, 2, false, false)]
    public void The_length_of_the_pregnancy_decides_how_many_doctors_form_the_opinion(int weeks, int typed, bool allowed, bool awaitingSecond)
    {
        var opinions = new[] { First, Second }.Take(typed).ToList();
        var mtp = new MtpCase();
        var result = mtp.Certify(
            Certification() with { WeeksOfGestation = weeks, Opinions = opinions, Category = weeks > 20 ? MtpRule3BCategory.ChangeOfMaritalStatus : null },
            Today, Certifier, Now);

        result.IsSuccess.ShouldBe(allowed, result.Error?.Message);
        if (!allowed)
        {
            // From 20 weeks the second opinion is the second doctor's to record, never typed (maker-checker, slice 9).
            result.Error!.Code.ShouldBe("mtp.second_opinion_typed");
        }
        else
        {
            mtp.AwaitingSecondOpinion.ShouldBe(awaitingSecond);
        }
    }

    [Fact]
    public void From_20_weeks_the_second_doctor_records_their_own_opinion_before_the_termination()
    {
        var mtp = new MtpCase();
        mtp.Certify(Certification() with { WeeksOfGestation = 22, Category = MtpRule3BCategory.Minor, AgeYears = 16, ConsentBy = MtpConsentBy.Guardian,
            GuardianName = "Selvi", GuardianRelation = "Mother" }, Today, Certifier, Now).IsSuccess.ShouldBeTrue();
        mtp.Terminate(Termination(), Today, Now).Error!.Code.ShouldBe("mtp.second_opinion_required");

        // Not the doctor who certified the case, nor the doctor of the first opinion under another account.
        mtp.RecordSecondOpinion(new MtpSecondOpinion(Certifier, "Dr. Meera", "TNMC 99999", Today, Certifier), Today).Error!.Code
            .ShouldBe("mtp.second_opinion_same_doctor");
        mtp.RecordSecondOpinion(new MtpSecondOpinion(SecondDoctor, "Dr. Meera K", " tnmc 45120 ", Today, SecondDoctor), Today).Error!.Code
            .ShouldBe("mtp.second_opinion_same_doctor");
        mtp.RecordSecondOpinion(new MtpSecondOpinion(SecondDoctor, "Dr. Lata", "TNMC 51877", Today.AddDays(1), SecondDoctor), Today).Error!.Fields!.Keys
            .ShouldBe(["opinionOn"]);

        mtp.RecordSecondOpinion(new MtpSecondOpinion(SecondDoctor, " Dr. Lata ", "TNMC 51877", Today, Certifier), Today).IsSuccess.ShouldBeTrue();
        mtp.AwaitingSecondOpinion.ShouldBeFalse();
        mtp.SecondOpinion.ShouldBe(new MtpOpinion("Dr. Lata", "TNMC 51877", Today, SecondDoctor, Certifier));
        mtp.Opinions.Count.ShouldBe(2);
        mtp.RecordSecondOpinion(new MtpSecondOpinion(Guid.NewGuid(), "Dr. Rao", "TNMC 1", Today, Certifier), Today).Error!.Code
            .ShouldBe("mtp.second_opinion_recorded");

        // A correction drops it: the second doctor gives it again on the case as corrected.
        mtp.Certify(Certification() with { WeeksOfGestation = 23, Category = MtpRule3BCategory.Minor, AgeYears = 16, ConsentBy = MtpConsentBy.Guardian,
            GuardianName = "Selvi", GuardianRelation = "Mother", Opinions = [First with { UserId = SecondDoctor }] }, Today, Certifier, Now).IsSuccess.ShouldBeTrue();
        (mtp.SecondOpinion, mtp.AwaitingSecondOpinion).ShouldBe(((MtpOpinion?)null, true), "an opinion typed on the case is never the doctor's own");
        mtp.RecordSecondOpinion(new MtpSecondOpinion(SecondDoctor, "Dr. Lata", "TNMC 51877", Today, SecondDoctor), Today).IsSuccess.ShouldBeTrue();
        mtp.Terminate(Termination(), Today, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Up_to_20_weeks_no_second_opinion_is_recorded() =>
        CertifiedCase().RecordSecondOpinion(new MtpSecondOpinion(SecondDoctor, "Dr. Lata", "TNMC 51877", Today, SecondDoctor), Today).Error!.Code
            .ShouldBe("mtp.second_opinion_not_needed");

    private static MtpCase CertifiedCase()
    {
        var mtp = new MtpCase();
        mtp.Certify(Certification(), Today, Certifier, Now).IsSuccess.ShouldBeTrue();
        return mtp;
    }

    [Fact]
    public void From_20_to_24_weeks_the_woman_is_in_a_rule_3b_category_and_a_contraceptive_failure_is_no_ground()
    {
        var late = Certification() with { WeeksOfGestation = 22, Opinions = [First] };

        new MtpCase().Certify(late, Today, null, Now).Error!.Fields!.Keys.ShouldBe(["category"]);
        new MtpCase().Certify(late with { Category = MtpRule3BCategory.Minor, Grounds = [MtpGround.ContraceptiveFailure] }, Today, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["grounds"]);
        new MtpCase().Certify(Certification() with { Opinions = [First, First with { DoctorName = "Dr. Meera R" }] }, Today, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["opinions"], "two opinions typed are of two doctors");
    }

    [Fact]
    public void Beyond_24_weeks_only_a_foetal_abnormality_the_medical_board_found()
    {
        var board = Certification() with { WeeksOfGestation = 28, Opinions = [First], Grounds = [MtpGround.FoetalAbnormality] };

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
