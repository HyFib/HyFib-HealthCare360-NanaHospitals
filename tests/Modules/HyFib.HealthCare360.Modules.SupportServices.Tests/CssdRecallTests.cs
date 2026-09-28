using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class CssdRecallTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 4, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(IndicatorResult.Pending)]
    [InlineData(IndicatorResult.Pass)]
    public void Nothing_is_recalled_unless_the_biological_indicator_failed(IndicatorResult biological)
    {
        var plan = CssdRecall.Plan(biological, wasReleased: true, [new PackSnapshot(Guid.NewGuid(), PackStatus.Available)],
            [new IssueSnapshot(Guid.NewGuid(), Guid.NewGuid(), SetIssueStatus.Issued, null, false)]);

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_load_that_never_left_cssd_needs_no_recall() =>
        CssdRecall.Plan(IndicatorResult.Fail, wasReleased: false, [new PackSnapshot(Guid.NewGuid(), PackStatus.Reprocess)], []).IsEmpty.ShouldBeTrue();

    [Fact]
    public void A_failed_released_load_flags_every_issue_and_pulls_back_every_pack_in_circulation()
    {
        var onShelf = Guid.NewGuid();
        var onWard = Guid.NewGuid();
        var usedAndReturned = Guid.NewGuid();
        var reprocess = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var issuedToWard = Guid.NewGuid();
        var usedOnPatient = Guid.NewGuid();
        var alreadyFlagged = Guid.NewGuid();

        var plan = CssdRecall.Plan(
            IndicatorResult.Fail,
            wasReleased: true,
            [
                new PackSnapshot(onShelf, PackStatus.Available),
                new PackSnapshot(onWard, PackStatus.Issued),
                new PackSnapshot(usedAndReturned, PackStatus.Returned),
                new PackSnapshot(reprocess, PackStatus.Reprocess),
            ],
            [
                new IssueSnapshot(issuedToWard, onWard, SetIssueStatus.Issued, null, false),
                new IssueSnapshot(usedOnPatient, usedAndReturned, SetIssueStatus.Returned, patient, false),
                new IssueSnapshot(alreadyFlagged, Guid.NewGuid(), SetIssueStatus.Recalled, null, true),
            ]);

        plan.IssuesToFlag.ShouldBe([issuedToWard, usedOnPatient], ignoreOrder: true);
        plan.PacksToRecall.ShouldBe([onShelf, onWard], ignoreOrder: true);
        plan.PatientsExposed.ShouldBe([patient]);
    }

    [Fact]
    public void A_released_load_whose_biological_indicator_fails_requires_a_recall()
    {
        var load = ReleasedLoad();

        load.RecordBiologicalIndicator(IndicatorResult.Fail, "BI-778", Now.AddHours(24)).IsSuccess.ShouldBeTrue();

        load.Status.ShouldBe(LoadStatus.Failed);
        load.FailureReason.ShouldBe("Biological indicator failed.");
        load.RequiresRecall.ShouldBeTrue();
    }

    [Fact]
    public void Flagging_an_unused_pack_stops_its_use()
    {
        var issue = new SetIssue { Status = SetIssueStatus.Issued, SterileUntil = new DateOnly(2026, 10, 2) };

        issue.FlagRecall("Load NHCS26000012: Biological indicator failed.", Now);

        issue.Status.ShouldBe(SetIssueStatus.Recalled);
        issue.IsRecallOpen.ShouldBeTrue();
        issue.RecordUse(Guid.NewGuid(), Now, new DateOnly(2026, 9, 25)).Error!.Code.ShouldBe("cssd.pack_recalled");
    }

    [Fact]
    public void A_pack_already_used_keeps_its_patient_link_when_flagged()
    {
        var patient = Guid.NewGuid();
        var issue = new SetIssue { Status = SetIssueStatus.Issued };
        issue.RecordUse(patient, Now, new DateOnly(2026, 9, 25)).IsSuccess.ShouldBeTrue();

        issue.FlagRecall("BI failed", Now.AddHours(20));

        issue.Status.ShouldBe(SetIssueStatus.Used);
        issue.PatientId.ShouldBe(patient);
        issue.RecallFlaggedAt.ShouldNotBeNull();
    }

    [Fact]
    public void A_recall_is_acknowledged_once_with_the_action_taken()
    {
        var issue = new SetIssue { Status = SetIssueStatus.Issued };
        issue.AcknowledgeRecall(null, null, "x", Now).Error!.Code.ShouldBe("cssd.no_recall");
        issue.FlagRecall("BI failed", Now);

        issue.AcknowledgeRecall(Guid.NewGuid(), "OT in-charge", "Pack returned unopened", Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        issue.IsRecallOpen.ShouldBeFalse();
        issue.RecallAction.ShouldBe("Pack returned unopened");
        issue.AcknowledgeRecall(null, null, "again", Now.AddHours(2)).Error!.Code.ShouldBe("cssd.recall_acknowledged");
    }

    private static SterilizationLoad ReleasedLoad()
    {
        var load = new SterilizationLoad { CycleNumber = "NHCS26000012", Method = SterilizationMethod.Steam, Status = LoadStatus.Loaded };
        load.Packs.Add(new SterilePack { PackNumber = "NHCS26000012-001", ShelfLifeDays = 7, Status = PackStatus.InProcess });
        load.Start(Now);
        load.Complete(134m, 210m, 4, IndicatorResult.Pass, Now.AddMinutes(45));
        load.Release(null, "CSSD in-charge", new DateOnly(2026, 9, 25), Now.AddMinutes(50));
        return load;
    }
}
