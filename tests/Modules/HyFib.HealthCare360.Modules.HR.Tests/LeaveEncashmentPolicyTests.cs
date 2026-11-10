using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;

namespace HyFib.HealthCare360.Modules.HR.Tests;

/// <summary>
/// The leave policy for in-service encashment, with worked examples. The policy below is the one the examples share:
/// earned leave, encashable, at most 10 days a year in service, and 15 days kept in the balance after encashing.
/// </summary>
public sealed class LeaveEncashmentPolicyTests
{
    private static LeaveType EarnedLeave(decimal? cap = 10, decimal keep = 15, bool halfDays = false, bool encashable = true) => new()
    {
        Code = "EL",
        Name = "Earned Leave",
        IsEncashable = encashable,
        InServiceEncashmentDaysPerYear = cap,
        MinimumBalanceAfterEncashment = keep,
        AllowHalfDay = halfDays,
    };

    [Fact]
    public void A_balance_of_30_with_15_kept_and_a_cap_of_10_encashes_up_to_10()
    {
        // 30 − 15 kept = 15 above the minimum, but the cap is 10.
        var position = new EncashmentPosition(Closing: 30, PendingLeave: 0, PendingEncashment: 0, EncashedThisYear: 0);
        LeaveEncashmentPolicy.MostDays(EarnedLeave(), position).ShouldBe(10);
        LeaveEncashmentPolicy.Check(EarnedLeave(), position, 10).ShouldBeNull();
    }

    [Fact]
    public void Days_already_encashed_this_year_count_against_the_cap()
    {
        // 6 encashed in March: 4 more under the cap of 10.
        var position = new EncashmentPosition(Closing: 40, PendingLeave: 0, PendingEncashment: 0, EncashedThisYear: 6);
        LeaveEncashmentPolicy.MostDays(EarnedLeave(), position).ShouldBe(4);
        LeaveEncashmentPolicy.Check(EarnedLeave(), position, 5)!.Code.ShouldBe(LeaveEncashmentPolicy.OverCapCode);
        LeaveEncashmentPolicy.Check(EarnedLeave(), position, 4).ShouldBeNull();
    }

    [Fact]
    public void Leave_and_encashments_waiting_are_reserved_before_the_minimum_is_checked()
    {
        // 24 in the balance, 3 days of leave applied for and 2 days of encashment waiting: 24 − 3 − 2 − 15 kept = 4 left to
        // encash; and 2 waiting leaves 8 under the cap, so the minimum decides.
        var position = new EncashmentPosition(Closing: 24, PendingLeave: 3, PendingEncashment: 2, EncashedThisYear: 0);
        LeaveEncashmentPolicy.MostDays(EarnedLeave(), position).ShouldBe(4);
        LeaveEncashmentPolicy.Check(EarnedLeave(), position, 5)!.Code.ShouldBe(LeaveEncashmentPolicy.BelowMinimumCode);
        LeaveEncashmentPolicy.Check(EarnedLeave(), position, 4).ShouldBeNull();
    }

    [Fact]
    public void A_balance_at_or_below_the_minimum_encashes_nothing()
    {
        var position = new EncashmentPosition(Closing: 12, PendingLeave: 0, PendingEncashment: 0, EncashedThisYear: 0);
        LeaveEncashmentPolicy.MostDays(EarnedLeave(), position).ShouldBe(0);
        LeaveEncashmentPolicy.Check(EarnedLeave(), position, 1)!.Code.ShouldBe(LeaveEncashmentPolicy.BelowMinimumCode);
    }

    [Fact]
    public void A_type_with_no_in_service_cap_or_not_encashable_pays_out_only_on_leaving()
    {
        var position = new EncashmentPosition(Closing: 60, PendingLeave: 0, PendingEncashment: 0, EncashedThisYear: 0);
        LeaveEncashmentPolicy.MostDays(EarnedLeave(cap: null), position).ShouldBeNull();
        LeaveEncashmentPolicy.Check(EarnedLeave(cap: null), position, 1)!.Code.ShouldBe(LeaveEncashmentPolicy.NotAllowedCode);
        LeaveEncashmentPolicy.Check(EarnedLeave(encashable: false), position, 1)!.Code.ShouldBe(LeaveEncashmentPolicy.NotAllowedCode);
    }

    [Fact]
    public void Whole_days_unless_the_type_allows_half_days()
    {
        var position = new EncashmentPosition(Closing: 30, PendingLeave: 0, PendingEncashment: 0, EncashedThisYear: 0);
        LeaveEncashmentPolicy.Check(EarnedLeave(), position, 2.5m)!.Code.ShouldBe(LeaveEncashmentPolicy.WholeDaysCode);
        LeaveEncashmentPolicy.Check(EarnedLeave(halfDays: true), position, 2.5m).ShouldBeNull();

        // 15.5 above the minimum, rounded down to whole days without half days.
        var odd = new EncashmentPosition(Closing: 25.5m, PendingLeave: 0, PendingEncashment: 0, EncashedThisYear: 0);
        LeaveEncashmentPolicy.MostDays(EarnedLeave(cap: 30), odd).ShouldBe(10);
        LeaveEncashmentPolicy.MostDays(EarnedLeave(cap: 30, halfDays: true), odd).ShouldBe(10.5m);
    }

    [Fact]
    public void In_service_encashment_is_wholly_taxable_at_wages_over_30()
    {
        // ₹36,000 a month of wages: ₹1,200 a day; 10 days are ₹12,000, none of it exempt (s.10(10AA) is only on leaving).
        var worked = LeaveEncashment.Compute(10, 36_000m, 30m, onLeaving: false);
        (worked.DailyRate, worked.Amount, worked.Exempt, worked.Taxable).ShouldBe((1_200m, 12_000m, 0m, 12_000m));
    }
}
