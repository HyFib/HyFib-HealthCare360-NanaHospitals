using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class ApprovalWorkflowTests
{
    private static readonly Guid Employee = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static LeaveRequest Leave(DateOnly from, DateOnly to, ApprovalStatus status = ApprovalStatus.Pending) => new()
    {
        EmployeeId = Guid.NewGuid(),
        FromDate = from,
        ToDate = to,
        Days = to.DayNumber - from.DayNumber + 1,
        Status = status,
        RequestedByUserId = Employee,
    };

    [Fact]
    public void A_manager_approves_a_pending_leave()
    {
        var leave = Leave(Today.AddDays(3), Today.AddDays(4));

        leave.Approve(Manager, Now, "Enjoy").IsSuccess.ShouldBeTrue();

        leave.Status.ShouldBe(ApprovalStatus.Approved);
        leave.DecidedByUserId.ShouldBe(Manager);
        leave.DecidedAt.ShouldBe(Now);
        leave.DecisionNote.ShouldBe("Enjoy");
    }

    [Fact]
    public void Nobody_approves_their_own_leave()
    {
        var result = Leave(Today.AddDays(3), Today.AddDays(4)).Approve(Employee, Now, null);

        result.Error!.Code.ShouldBe("approval.self");
        result.Error.Type.ShouldBe(HyFib.HealthCare360.SharedKernel.ErrorType.Forbidden);
    }

    [Fact]
    public void A_decided_request_cannot_be_decided_again()
    {
        var leave = Leave(Today.AddDays(3), Today.AddDays(4));
        leave.Approve(Manager, Now, null);

        leave.Reject(Manager, Now, "Changed my mind").Error!.Code.ShouldBe("approval.not_pending");
    }

    [Fact]
    public void Rejection_needs_a_reason()
    {
        var leave = Leave(Today.AddDays(3), Today.AddDays(4));

        leave.Reject(Manager, Now, " ").IsFailure.ShouldBeTrue();
        leave.Status.ShouldBe(ApprovalStatus.Pending);
    }

    [Fact]
    public void Employees_can_withdraw_pending_leave()
    {
        var leave = Leave(Today.AddDays(-2), Today);

        leave.Cancel("Not needed", Today, byHr: false, Now).IsSuccess.ShouldBeTrue();
        leave.Status.ShouldBe(ApprovalStatus.Cancelled);
        leave.CancelReason.ShouldBe("Not needed");
    }

    [Fact]
    public void Approved_leave_that_has_started_can_only_be_cancelled_by_hr()
    {
        var leave = Leave(Today.AddDays(-1), Today.AddDays(1), ApprovalStatus.Approved);

        leave.Cancel("Back early", Today, byHr: false, Now).Error!.Code.ShouldBe("leave.started");
        leave.Cancel("Back early", Today, byHr: true, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Approved_future_leave_can_be_withdrawn_by_the_employee()
    {
        Leave(Today.AddDays(5), Today.AddDays(6), ApprovalStatus.Approved).Cancel("Trip cancelled", Today, byHr: false, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Rejected_leave_cannot_be_cancelled()
    {
        Leave(Today.AddDays(5), Today.AddDays(6), ApprovalStatus.Rejected).Cancel("x", Today, byHr: true, Now).Error!.Code.ShouldBe("leave.closed");
    }

    [Fact]
    public void Portion_of_each_day_follows_the_sessions()
    {
        var leave = Leave(Today, Today.AddDays(2));
        leave.FromSession = LeaveSession.SecondHalf;
        leave.ToSession = LeaveSession.FirstHalf;

        leave.PortionOn(Today.AddDays(-1)).ShouldBe(0m);
        leave.PortionOn(Today).ShouldBe(0.5m);
        leave.PortionOn(Today.AddDays(1)).ShouldBe(1m);
        leave.PortionOn(Today.AddDays(2)).ShouldBe(0.5m);
        leave.PortionOn(Today.AddDays(3)).ShouldBe(0m);
    }

    [Fact]
    public void Approved_missed_punch_supplies_the_corrected_times()
    {
        var request = new AttendanceRegularisation
        {
            Date = Today,
            Kind = RegularisationKind.MissedPunch,
            RequestedIn = new TimeOnly(9, 0),
            RequestedOut = new TimeOnly(17, 30),
            Reason = "Terminal offline",
            RequestedByUserId = Employee,
        };

        request.RequestedPunches().ShouldBeEmpty();
        request.Approve(Manager, Now, null).IsSuccess.ShouldBeTrue();

        request.RequestedPunches().ShouldBe([Today.ToDateTime(new TimeOnly(9, 0)), Today.ToDateTime(new TimeOnly(17, 30))]);
    }

    [Fact]
    public void A_night_shift_out_time_before_the_in_time_is_on_the_next_day()
    {
        var request = new AttendanceRegularisation
        {
            Date = Today,
            Kind = RegularisationKind.MissedPunch,
            RequestedIn = new TimeOnly(21, 0),
            RequestedOut = new TimeOnly(7, 0),
            Status = ApprovalStatus.Approved,
        };

        request.RequestedPunches()[1].ShouldBe(Today.AddDays(1).ToDateTime(new TimeOnly(7, 0)));
    }

    [Fact]
    public void Regularisations_follow_the_same_approval_rules()
    {
        var own = new AttendanceRegularisation { RequestedByUserId = Manager };
        own.Approve(Manager, Now, null).Error!.Code.ShouldBe("approval.self");
        own.Reject(Guid.NewGuid(), Now, null).IsFailure.ShouldBeTrue();

        own.Cancel().IsSuccess.ShouldBeTrue();
        own.Cancel().IsFailure.ShouldBeTrue();
    }
}
