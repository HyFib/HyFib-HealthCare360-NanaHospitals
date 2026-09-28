using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class ClinicalOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

    private static readonly ServiceItem Cbc = new() { Code = "LAB-CBC", Name = "Complete blood count", Category = ServiceCategory.Laboratory };
    private static readonly ServiceItem ChestXray = new() { Code = "RAD-XR-CHEST", Name = "X-ray chest PA view", Category = ServiceCategory.Radiology };
    private static readonly ServiceItem Prbc = new() { Code = "BB-PRBC", Name = "Packed red blood cells", Category = ServiceCategory.BloodBank };
    private static readonly ServiceItem PhysioSession = new() { Code = "PHY-EXERCISE", Name = "Exercise therapy", Category = ServiceCategory.Physiotherapy };

    private const string PackageNote = "Health checkup HC-EXEC (NHHC26000001): package component — bill under the package.";

    private static ClinicalOrder CheckupOrder(Guid bookingId, params ServiceItem[] services)
    {
        var order = new ClinicalOrder
        {
            FacilityId = Guid.CreateVersion7(),
            OrderNumber = "NHOR260000002",
            PatientId = Guid.CreateVersion7(),
            Priority = OrderPriority.Routine,
            Source = OrderSource.HealthCheckup,
            PackageBookingId = bookingId,
            ClinicalNotes = PackageNote,
        };
        foreach (var service in services)
        {
            order.AddItem(service, null).IsSuccess.ShouldBeTrue();
        }

        return order;
    }

    private static ClinicalOrder NewOrder(params ServiceItem[] services)
    {
        var order = new ClinicalOrder
        {
            FacilityId = Guid.CreateVersion7(),
            OrderNumber = "NHOR260000001",
            PatientId = Guid.CreateVersion7(),
            VisitId = Guid.CreateVersion7(),
            OrderingPractitionerId = Guid.CreateVersion7(),
            Priority = OrderPriority.Urgent,
        };
        foreach (var service in services)
        {
            order.AddItem(service, "  rule out infection  ");
        }

        return order;
    }

    [Fact]
    public void Placing_raises_one_event_per_line_with_snapshots()
    {
        var order = NewOrder(Cbc, ChestXray);

        order.Place(Now).IsSuccess.ShouldBeTrue();

        var events = order.DomainEvents.OfType<ServiceOrderPlaced>().ToList();
        events.Count.ShouldBe(2);
        events[0].OrderItemId.ShouldBe(order.Items[0].Id);
        events[0].ServiceCode.ShouldBe("LAB-CBC");
        events[0].Category.ShouldBe(ServiceCategory.Laboratory);
        events[0].Priority.ShouldBe(OrderPriority.Urgent);
        events[0].OrderNumber.ShouldBe("NHOR260000001");
        events[0].ClinicalNotes.ShouldBe("rule out infection");
        events[1].ServiceName.ShouldBe("X-ray chest PA view");
        events.ShouldAllBe(e => e.PackageBookingId == null && !e.IsPackageComponent && e.Quantity == 1);
        order.OrderedAt.ShouldBe(Now);
        order.Status.ShouldBe(ClinicalOrderStatus.Active);
    }

    [Fact]
    public void Checkup_tests_carry_the_booking_so_billing_charges_the_package_once()
    {
        var bookingId = Guid.CreateVersion7();
        var order = CheckupOrder(bookingId, Cbc, ChestXray);

        order.Place(Now).IsSuccess.ShouldBeTrue();

        var events = order.DomainEvents.OfType<ServiceOrderPlaced>().ToList();
        events.Count.ShouldBe(2);
        events.ShouldAllBe(e => e.PackageBookingId == bookingId && e.IsPackageComponent && e.Quantity == 1);
        events.ShouldAllBe(e => e.ClinicalNotes == PackageNote);
    }

    [Fact]
    public void Package_link_and_order_source_must_agree()
    {
        var unlinked = CheckupOrder(Guid.CreateVersion7(), Cbc);
        unlinked.PackageBookingId = null;
        var stray = NewOrder(Cbc);
        stray.PackageBookingId = Guid.CreateVersion7();

        unlinked.Place(Now).Error!.Code.ShouldBe("order.package_link");
        stray.Place(Now).Error!.Code.ShouldBe("order.package_link");

        unlinked.DomainEvents.ShouldBeEmpty();
        stray.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Blood_units_and_therapy_sessions_are_ordered_in_quantity()
    {
        var order = NewOrder();
        order.AddItem(Prbc, "Hb 6.2 g/dL, symptomatic", 2).IsSuccess.ShouldBeTrue();
        order.AddItem(PhysioSession, null, 10).IsSuccess.ShouldBeTrue();

        order.Place(Now).IsSuccess.ShouldBeTrue();

        order.Items.Select(i => i.Quantity).ShouldBe([2, 10]);
        order.DomainEvents.OfType<ServiceOrderPlaced>().Select(e => e.Quantity).ShouldBe([2, 10]);
    }

    [Theory]
    [InlineData(ServiceCategory.Laboratory, 2)]
    [InlineData(ServiceCategory.Radiology, 3)]
    [InlineData(ServiceCategory.BloodBank, 11)]
    [InlineData(ServiceCategory.Physiotherapy, 31)]
    [InlineData(ServiceCategory.Dialysis, 0)]
    [InlineData(ServiceCategory.BloodBank, -1)]
    public void Quantities_outside_the_policy_are_refused(ServiceCategory category, int quantity)
    {
        var order = NewOrder();

        order.AddItem(new ServiceItem { Code = "SVC", Name = "Service", Category = category }, null, quantity).Error!.Code.ShouldBe("order_item.quantity");

        order.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ServiceCategory.BloodBank, 10)]
    [InlineData(ServiceCategory.Physiotherapy, 30)]
    [InlineData(ServiceCategory.Dialysis, 30)]
    [InlineData(ServiceCategory.Laboratory, 1)]
    public void Quantity_caps_follow_the_category(ServiceCategory category, int max) => OrderQuantity.MaxFor(category).ShouldBe(max);

    [Fact]
    public void Empty_or_unnumbered_orders_cannot_be_placed()
    {
        NewOrder().Place(Now).Error!.Code.ShouldBe("order.empty");

        var unnumbered = NewOrder(Cbc);
        unnumbered.OrderNumber = string.Empty;
        unnumbered.Place(Now).Error!.Code.ShouldBe("order.number_missing");
    }

    [Fact]
    public void Cancelling_a_line_notifies_the_performing_department()
    {
        var order = NewOrder(Cbc, ChestXray);
        order.Place(Now);
        order.ClearDomainEvents();

        order.CancelItem(order.Items[0].Id, "Ordered in error", Now).IsSuccess.ShouldBeTrue();

        order.DomainEvents.OfType<ServiceOrderCancelled>().ShouldHaveSingleItem().Reason.ShouldBe("Ordered in error");
        order.Items[0].Status.ShouldBe(OrderItemStatus.Cancelled);
        order.Status.ShouldBe(ClinicalOrderStatus.Active);
    }

    [Fact]
    public void Completed_lines_cannot_be_cancelled()
    {
        var order = NewOrder(Cbc, ChestXray);
        order.Place(Now);
        order.UpdateItemStatus(order.Items[0].Id, OrderItemStatus.Completed, Now, "report-1").IsSuccess.ShouldBeTrue();

        order.CancelItem(order.Items[0].Id, "No longer needed", Now).Error!.Code.ShouldBe("order_item.closed");
        order.Items[0].ResultReference.ShouldBe("report-1");
    }

    [Fact]
    public void Cancelling_the_order_skips_completed_lines()
    {
        var order = NewOrder(Cbc, ChestXray);
        order.Place(Now);
        order.UpdateItemStatus(order.Items[0].Id, OrderItemStatus.Completed, Now);
        order.ClearDomainEvents();

        order.Cancel("Patient discharged", Now).IsSuccess.ShouldBeTrue();

        order.DomainEvents.OfType<ServiceOrderCancelled>().ShouldHaveSingleItem().OrderItemId.ShouldBe(order.Items[1].Id);
        order.Status.ShouldBe(ClinicalOrderStatus.Completed);
        order.Cancel("Again", Now).Error!.Code.ShouldBe("order.closed");
    }

    [Fact]
    public void Line_progress_follows_ordered_in_progress_completed()
    {
        var order = NewOrder(Cbc);
        order.Place(Now);
        var line = order.Items[0];

        order.UpdateItemStatus(line.Id, OrderItemStatus.InProgress, Now).IsSuccess.ShouldBeTrue();
        order.UpdateItemStatus(line.Id, OrderItemStatus.InProgress, Now).Error!.Code.ShouldBe("order_item.not_ordered");
        order.UpdateItemStatus(line.Id, OrderItemStatus.Cancelled, Now).Error!.Code.ShouldBe("order_item.invalid_status");
        order.UpdateItemStatus(line.Id, OrderItemStatus.Completed, Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(ClinicalOrderStatus.Completed);
    }

    [Theory]
    [InlineData(new[] { OrderItemStatus.Ordered, OrderItemStatus.Completed }, ClinicalOrderStatus.Active)]
    [InlineData(new[] { OrderItemStatus.Cancelled, OrderItemStatus.Cancelled }, ClinicalOrderStatus.Cancelled)]
    [InlineData(new[] { OrderItemStatus.Completed, OrderItemStatus.Cancelled }, ClinicalOrderStatus.Completed)]
    [InlineData(new OrderItemStatus[0], ClinicalOrderStatus.Active)]
    public void Order_status_is_derived_from_lines(OrderItemStatus[] lines, ClinicalOrderStatus expected) =>
        ClinicalOrder.DeriveStatus(lines).ShouldBe(expected);
}
