using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class SterilizationLoadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static SterilizationLoad NewLoad(SterilizationMethod method = SterilizationMethod.Steam, bool implants = false, int packs = 2)
    {
        var load = new SterilizationLoad { CycleNumber = "NHCS26000001", Method = method, ContainsImplants = implants, Status = LoadStatus.Loaded };
        for (var i = 1; i <= packs; i++)
        {
            load.Packs.Add(new SterilePack { PackNumber = $"NHCS26000001-{i:000}", ShelfLifeDays = 7, Status = PackStatus.InProcess });
        }

        return load;
    }

    [Fact]
    public void An_empty_load_cannot_be_started() =>
        NewLoad(packs: 0).Start(Now).Error!.Code.ShouldBe("cssd.load_empty");

    [Fact]
    public void A_cycle_with_valid_parameters_and_a_passed_chemical_indicator_completes()
    {
        var load = NewLoad();
        load.Start(Now);

        load.Complete(134m, 210m, 4, IndicatorResult.Pass, Now.AddMinutes(40)).IsSuccess.ShouldBeTrue();

        load.Status.ShouldBe(LoadStatus.Completed);
        load.ExposureMinutes.ShouldBe(4);
    }

    [Fact]
    public void A_cycle_outside_the_validated_parameters_fails_and_its_packs_are_reprocessed()
    {
        var load = NewLoad();
        load.Start(Now);

        load.Complete(121m, null, 10, IndicatorResult.Pass, Now.AddMinutes(40)).IsSuccess.ShouldBeTrue();

        load.Status.ShouldBe(LoadStatus.Failed);
        load.FailureReason!.ShouldContain("15 minutes");
        load.Packs.ShouldAllBe(p => p.Status == PackStatus.Reprocess);
        load.RequiresRecall.ShouldBeFalse();
    }

    [Fact]
    public void A_failed_chemical_indicator_fails_the_cycle()
    {
        var load = NewLoad();
        load.Start(Now);

        load.Complete(134m, 210m, 5, IndicatorResult.Fail, Now.AddMinutes(40));

        load.Status.ShouldBe(LoadStatus.Failed);
        load.FailureReason.ShouldBe("Chemical indicator failed.");
    }

    [Fact]
    public void Completion_needs_a_running_cycle_and_a_chemical_indicator_reading()
    {
        var load = NewLoad();
        load.Complete(134m, null, 4, IndicatorResult.Pass, Now).Error!.Code.ShouldBe("cssd.load_not_running");

        load.Start(Now);
        load.Complete(134m, null, 4, IndicatorResult.Pending, Now).Error!.Code.ShouldBe("cssd.ci_required");
    }

    [Fact]
    public void Release_dates_every_pack_by_its_shelf_life()
    {
        var load = NewLoad();
        load.Start(Now);
        load.Complete(134m, 210m, 4, IndicatorResult.Pass, Now.AddMinutes(40));

        load.Release(Guid.NewGuid(), "CSSD in-charge", Today, Now.AddMinutes(45)).IsSuccess.ShouldBeTrue();

        load.Status.ShouldBe(LoadStatus.Released);
        load.Packs.ShouldAllBe(p => p.Status == PackStatus.Available && p.SterileUntil == new DateOnly(2026, 10, 2));
        load.Release(null, null, Today, Now).Error!.Code.ShouldBe("cssd.load_not_releasable");
    }

    [Fact]
    public void Loads_with_implants_stay_quarantined_until_the_biological_indicator_passes()
    {
        var load = NewLoad(implants: true);
        load.Start(Now);
        load.Complete(134m, 210m, 4, IndicatorResult.Pass, Now.AddMinutes(40));

        load.Release(null, null, Today, Now.AddMinutes(45)).Error!.Code.ShouldBe("cssd.implant_quarantine");
        load.RecordBiologicalIndicator(IndicatorResult.Pass, "BI-1", Now.AddHours(3)).IsSuccess.ShouldBeTrue();
        load.Release(null, null, Today, Now.AddHours(3)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_biological_indicator_is_read_once_after_the_cycle()
    {
        var load = NewLoad();
        load.RecordBiologicalIndicator(IndicatorResult.Pass, null, Now).Error!.Code.ShouldBe("cssd.bi_too_early");

        load.Start(Now);
        load.Complete(134m, 210m, 4, IndicatorResult.Pass, Now.AddMinutes(40));
        load.RecordBiologicalIndicator(IndicatorResult.Pending, null, Now).Error!.Code.ShouldBe("cssd.bi_result_required");
        load.RecordBiologicalIndicator(IndicatorResult.Pass, null, Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        load.RecordBiologicalIndicator(IndicatorResult.Fail, null, Now.AddHours(3)).Error!.Code.ShouldBe("cssd.bi_recorded");
    }

    [Fact]
    public void An_unreleased_failed_load_needs_no_recall()
    {
        var load = NewLoad();
        load.Start(Now);
        load.Complete(134m, 210m, 4, IndicatorResult.Pass, Now.AddMinutes(40));

        load.RecordBiologicalIndicator(IndicatorResult.Fail, null, Now.AddHours(2));

        load.Status.ShouldBe(LoadStatus.Failed);
        load.RequiresRecall.ShouldBeFalse();
        load.Packs.ShouldAllBe(p => p.Status == PackStatus.Reprocess);
    }

    [Fact]
    public void A_cycle_can_be_aborted_before_release_only()
    {
        var load = NewLoad();
        load.Start(Now);
        load.Abort("Power failure mid-cycle", Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        load.Status.ShouldBe(LoadStatus.Failed);

        load.Abort("again", Now).Error!.Code.ShouldBe("cssd.load_closed");
    }

    [Theory]
    [InlineData(SterilizationMethod.Steam, 134, 3, true)]
    [InlineData(SterilizationMethod.Steam, 134, 2, false)]
    [InlineData(SterilizationMethod.Steam, 126, 10, true)]
    [InlineData(SterilizationMethod.Steam, 126, 9, false)]
    [InlineData(SterilizationMethod.Steam, 121, 15, true)]
    [InlineData(SterilizationMethod.Steam, 121, 14, false)]
    [InlineData(SterilizationMethod.Steam, 118, 60, false)]
    [InlineData(SterilizationMethod.Steam, 140, 10, false)]
    [InlineData(SterilizationMethod.EthyleneOxide, 55, 120, true)]
    [InlineData(SterilizationMethod.EthyleneOxide, 55, 30, false)]
    [InlineData(SterilizationMethod.EthyleneOxide, 70, 120, false)]
    [InlineData(SterilizationMethod.Plasma, 50, 28, true)]
    [InlineData(SterilizationMethod.Plasma, 50, 20, false)]
    [InlineData(SterilizationMethod.Plasma, 30, 60, false)]
    public void Validated_cycle_parameters(SterilizationMethod method, int temperatureC, int minutes, bool valid) =>
        SterilizationParameters.Check(method, temperatureC, minutes).IsWithinRange.ShouldBe(valid);

    [Fact]
    public void Only_sterile_unexpired_packs_can_be_issued()
    {
        var department = Guid.NewGuid();
        var pack = new SterilePack { PackNumber = "P-1", ShelfLifeDays = 7, Status = PackStatus.InProcess };
        pack.Issue(department, Today).Error!.Code.ShouldBe("cssd.pack_unavailable");

        pack.MarkSterile(Today);
        pack.Issue(department, Today.AddDays(8)).Error!.Code.ShouldBe("cssd.pack_expired");
        pack.Issue(department, Today.AddDays(7)).IsSuccess.ShouldBeTrue();

        pack.Status.ShouldBe(PackStatus.Issued);
        pack.IssuedToDepartmentId.ShouldBe(department);
    }

    [Fact]
    public void An_expired_pack_is_not_used_on_a_patient()
    {
        var issue = new SetIssue { Status = SetIssueStatus.Issued, SterileUntil = Today };

        issue.RecordUse(Guid.NewGuid(), Now, Today.AddDays(1)).Error!.Code.ShouldBe("cssd.pack_expired");
        issue.RecordUse(Guid.NewGuid(), Now, Today).IsSuccess.ShouldBeTrue();
    }
}
