namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class AssetMaintenanceTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pm_visit_moves_the_next_due_date_from_the_actual_visit()
    {
        var plan = new PreventiveMaintenancePlan { FrequencyDays = 90, NextDueDate = Today };

        plan.RecordVisit(Today.AddDays(3)).IsSuccess.ShouldBeTrue();

        plan.NextDueDate.ShouldBe(Today.AddDays(93));
        plan.LastDoneOn.ShouldBe(Today.AddDays(3));
        plan.RecordVisit(Today).Error!.Code.ShouldBe("pm.visit_out_of_order");
        plan.IsDue(Today, 7).ShouldBeFalse();
        plan.IsDue(Today.AddDays(90), 7).ShouldBeTrue();
    }

    [Fact]
    public void Ticket_life_cycle_records_downtime_from_the_report()
    {
        var ticket = new MaintenanceTicket { Number = "NHMT26000001", ReportedAt = Now };

        ticket.Assign("Biomedical engineer on call", null, null, Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        ticket.Status.ShouldBe(TicketStatus.Assigned);
        ticket.Start(Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();
        ticket.Close(Now).Error!.Code.ShouldBe("ticket.not_resolved");

        ticket.Resolve("Replaced occlusion sensor", "Sensor wear", 1800m, null, Now.AddHours(4)).IsSuccess.ShouldBeTrue();
        ticket.DowntimeMinutes.ShouldBe(240);
        ticket.Close(Now.AddHours(5)).IsSuccess.ShouldBeTrue();
        ticket.Assign("someone", null, null, Now).Error!.Code.ShouldBe("ticket.closed");
    }

    [Fact]
    public void Reopening_a_ticket_clears_the_resolution_timing()
    {
        var ticket = new MaintenanceTicket { ReportedAt = Now };
        ticket.Resolve("Reset", null, 0, 15, Now.AddHours(1));

        ticket.Reopen().IsSuccess.ShouldBeTrue();

        ticket.Status.ShouldBe(TicketStatus.InProgress);
        ticket.DowntimeMinutes.ShouldBeNull();
        ticket.Resolve("Fixed", null, -1, null, Now).Error!.Code.ShouldBe("ticket.invalid_values");
    }

    [Fact]
    public void Assets_move_through_maintenance_condemnation_and_disposal()
    {
        var asset = new Asset { AssetTag = "NHAS000001" };

        asset.MarkUnderMaintenance().IsSuccess.ShouldBeTrue();
        asset.SetOperational(inUse: false).Error!.Code.ShouldBe("asset.status_locked");
        asset.ReturnToService().IsSuccess.ShouldBeTrue();
        asset.SetOperational(inUse: false).IsSuccess.ShouldBeTrue();
        asset.Status.ShouldBe(AssetStatus.Idle);

        asset.Dispose(Today, 1000, "Auction").Error!.Code.ShouldBe("asset.not_condemned");
        asset.Condemn(Today).IsSuccess.ShouldBeTrue();
        asset.MarkUnderMaintenance().Error!.Code.ShouldBe("asset.retired");
        asset.Dispose(Today, -1, "Auction").IsFailure.ShouldBeTrue();
        asset.Dispose(Today, 1000, "Auction").IsSuccess.ShouldBeTrue();
        asset.IsInService.ShouldBeFalse();
    }

    [Fact]
    public void Condemnation_must_be_decided_by_someone_other_than_the_requester()
    {
        var requester = Guid.CreateVersion7();
        var request = new CondemnationRequest { RequestedByUserId = requester };

        request.Approve(requester, null, Now).Error!.Code.ShouldBe("condemnation.self_approval");
        request.Approve(Guid.CreateVersion7(), "Beyond economical repair", Now).IsSuccess.ShouldBeTrue();
        request.Reject(Guid.CreateVersion7(), "late", Now).Error!.Code.ShouldBe("condemnation.decided");
    }

    [Fact]
    public void Contracts_are_current_between_their_dates()
    {
        var contract = new AssetContract { StartDate = Today.AddDays(-10), EndDate = Today.AddDays(10) };

        contract.IsCurrent(Today).ShouldBeTrue();
        contract.IsCurrent(Today.AddDays(11)).ShouldBeFalse();
    }
}
