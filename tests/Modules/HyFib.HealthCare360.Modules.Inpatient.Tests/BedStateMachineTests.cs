using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class BedStateMachineTests
{
    [Theory]
    [InlineData(BedStatus.Vacant, BedStatus.Occupied)]
    [InlineData(BedStatus.Vacant, BedStatus.Reserved)]
    [InlineData(BedStatus.Vacant, BedStatus.Cleaning)]
    [InlineData(BedStatus.Reserved, BedStatus.Occupied)]
    [InlineData(BedStatus.Reserved, BedStatus.Vacant)]
    [InlineData(BedStatus.Occupied, BedStatus.Cleaning)]
    [InlineData(BedStatus.Cleaning, BedStatus.Vacant)]
    [InlineData(BedStatus.Cleaning, BedStatus.Maintenance)]
    [InlineData(BedStatus.Blocked, BedStatus.Vacant)]
    [InlineData(BedStatus.Blocked, BedStatus.Cleaning)]
    [InlineData(BedStatus.Maintenance, BedStatus.Cleaning)]
    public void Allowed_transitions_pass(BedStatus from, BedStatus to) =>
        BedStateMachine.Validate(from, to, "reason").IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(BedStatus.Occupied, BedStatus.Vacant)]
    [InlineData(BedStatus.Occupied, BedStatus.Reserved)]
    [InlineData(BedStatus.Occupied, BedStatus.Blocked)]
    [InlineData(BedStatus.Cleaning, BedStatus.Occupied)]
    [InlineData(BedStatus.Cleaning, BedStatus.Reserved)]
    [InlineData(BedStatus.Maintenance, BedStatus.Vacant)]
    [InlineData(BedStatus.Maintenance, BedStatus.Occupied)]
    [InlineData(BedStatus.Reserved, BedStatus.Blocked)]
    [InlineData(BedStatus.Blocked, BedStatus.Occupied)]
    public void Forbidden_transitions_fail(BedStatus from, BedStatus to)
    {
        var result = BedStateMachine.Validate(from, to, "reason");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("bed.invalid_transition");
    }

    [Theory]
    [InlineData(BedStatus.Blocked)]
    [InlineData(BedStatus.Maintenance)]
    public void Blocking_and_maintenance_need_a_reason(BedStatus to)
    {
        BedStateMachine.Validate(BedStatus.Vacant, to, "  ").Error!.Code.ShouldBe("bed.reason_required");
        BedStateMachine.Validate(BedStatus.Vacant, to, null).Error!.Code.ShouldBe("bed.reason_required");
        BedStateMachine.Validate(BedStatus.Vacant, to, "Oxygen port leaking").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Moving_to_the_same_status_is_rejected() =>
        BedStateMachine.Validate(BedStatus.Vacant, BedStatus.Vacant, null).Error!.Code.ShouldBe("bed.same_status");

    [Fact]
    public void An_occupied_bed_can_only_go_for_cleaning() =>
        BedStateMachine.NextStatuses(BedStatus.Occupied).ShouldBe([BedStatus.Cleaning]);

    [Fact]
    public void Every_status_has_a_path_back_to_vacant()
    {
        foreach (var start in Enum.GetValues<BedStatus>())
        {
            var seen = new HashSet<BedStatus> { start };
            var queue = new Queue<BedStatus>([start]);
            while (queue.Count > 0)
            {
                foreach (var next in BedStateMachine.NextStatuses(queue.Dequeue()).Where(seen.Add))
                {
                    queue.Enqueue(next);
                }
            }

            seen.ShouldContain(BedStatus.Vacant, $"{start} must be able to return to service");
        }
    }
}
