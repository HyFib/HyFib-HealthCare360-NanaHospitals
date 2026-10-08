using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>Washer-disinfector cycles: disinfection by A0, the inspection of the trays, and what may be packed (CSSD-04).</summary>
public sealed class WashCycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 4, 30, 0, TimeSpan.Zero);
    private static readonly Guid MajorSet = Guid.CreateVersion7();
    private static readonly Guid DressingSet = Guid.CreateVersion7();

    private static WashCycle Running() => new()
    {
        CycleNumber = "NHCW26000001",
        WasherName = "Washer-disinfector 1",
        Status = WashCycleStatus.Running,
        Trays = [new WashCycleTray { InstrumentSetId = MajorSet, Quantity = 2 }],
    };

    [Theory]
    [InlineData(90, 5, 3000)]
    [InlineData(90, 1, 600)]
    [InlineData(93, 10, 11971)]
    [InlineData(80, 10, 600)]
    public void A0_counts_each_second_at_ten_times_the_rate_for_every_ten_degrees_above_80(
        int temperatureC, int minutes, int a0) =>
        WashCycle.A0For(temperatureC, minutes).ShouldBe(a0);

    [Fact]
    public void A_cycle_that_disinfects_and_leaves_the_trays_clean_passes()
    {
        var cycle = Running();

        cycle.Complete(90m, 5m, inspectedClean: true, Now).IsSuccess.ShouldBeTrue();

        cycle.Status.ShouldBe(WashCycleStatus.Passed);
        cycle.A0.ShouldBe(3000);
        cycle.CompletedAt.ShouldBe(Now);
        cycle.CanPack(MajorSet, "Major set").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_cycle_below_the_required_a0_fails()
    {
        var cycle = Running();

        cycle.Complete(85m, 5m, inspectedClean: true, Now).IsSuccess.ShouldBeTrue();

        cycle.Status.ShouldBe(WashCycleStatus.Failed);
        cycle.A0.ShouldBe(948);
        cycle.FailureReason!.ShouldContain("A0 948");
        cycle.CanPack(MajorSet, "Major set").Error!.Code.ShouldBe("cssd.wash_not_passed");
    }

    [Fact]
    public void Trays_not_clean_on_inspection_fail_the_cycle_whatever_the_temperature()
    {
        var cycle = Running();

        cycle.Complete(93m, 10m, inspectedClean: false, Now).IsSuccess.ShouldBeTrue();

        cycle.Status.ShouldBe(WashCycleStatus.Failed);
        cycle.FailureReason.ShouldBe("The trays were not clean on inspection.");
    }

    [Fact]
    public void A_finished_cycle_cannot_be_completed_or_aborted_again()
    {
        var cycle = Running();
        cycle.Complete(90m, 5m, inspectedClean: true, Now);

        cycle.Complete(90m, 5m, inspectedClean: true, Now).Error!.Code.ShouldBe("cssd.wash_not_running");
        cycle.Abort("Door fault", Now).Error!.Code.ShouldBe("cssd.wash_closed");
        cycle.Status.ShouldBe(WashCycleStatus.Passed);
    }

    [Fact]
    public void An_aborted_cycle_fails_with_the_operators_reason()
    {
        var cycle = Running();

        cycle.Abort(" Power cut during the thermal stage ", Now).IsSuccess.ShouldBeTrue();

        cycle.Status.ShouldBe(WashCycleStatus.Failed);
        cycle.FailureReason.ShouldBe("Power cut during the thermal stage");
        cycle.CompletedAt.ShouldBe(Now);
    }

    [Fact]
    public void Only_the_sets_the_cycle_washed_may_be_packed_from_it()
    {
        var cycle = Running();
        cycle.Complete(90m, 5m, inspectedClean: true, Now);

        var refused = cycle.CanPack(DressingSet, "Dressing set");

        refused.Error!.Code.ShouldBe("cssd.wash_set_mismatch");
        refused.Error.Message.ShouldBe("Dressing set was not washed in cycle NHCW26000001.");
    }

    [Fact]
    public void A_cycle_gives_one_pack_for_each_tray_it_washed()
    {
        var cycle = Running();
        cycle.Complete(90m, 5m, inspectedClean: true, Now);

        cycle.CanPack(MajorSet, "Major set", quantity: 2).IsSuccess.ShouldBeTrue();
        cycle.CanPack(MajorSet, "Major set", quantity: 1, alreadyPacked: 1).IsSuccess.ShouldBeTrue();

        var tooMany = cycle.CanPack(MajorSet, "Major set", quantity: 3);
        tooMany.Error!.Code.ShouldBe("cssd.wash_trays_used");
        tooMany.Error.Message.ShouldBe("Wash cycle NHCW26000001 washed 2 trays of Major set: pack at most 2 from it.");

        cycle.CanPack(MajorSet, "Major set", quantity: 2, alreadyPacked: 1).Error!.Message
            .ShouldBe("Wash cycle NHCW26000001 washed 2 trays of Major set, and 1 is already packed: pack at most 1 more from it.");
        cycle.CanPack(MajorSet, "Major set", quantity: 1, alreadyPacked: 2).Error!.Message
            .ShouldBe("The 2 trays of Major set washed in cycle NHCW26000001 are already packed. Wash any others in a new cycle.");
    }

    [Fact]
    public void Trays_of_a_running_cycle_cannot_be_packed_yet() =>
        Running().CanPack(MajorSet, "Major set").Error!.Message.ShouldContain("has not finished");
}
