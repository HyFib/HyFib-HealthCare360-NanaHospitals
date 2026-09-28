using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Services;

/// <summary>EMR orders become work items; the order quantity carries units of blood and prescribed physiotherapy sessions.</summary>
public sealed class OrderHandlerTests
{
    private static ServiceOrderPlaced Order(ServiceCategory category, string code, int quantity = 1, OrderPriority priority = OrderPriority.Routine, string? notes = null) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "NHORD26000042", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), code, "Service " + code, category, priority, Guid.CreateVersion7(), notes, TestData.Now, Quantity: quantity);

    [Fact]
    public void Blood_order_quantity_is_the_number_of_units_requested()
    {
        var order = Order(ServiceCategory.BloodBank, "BB-PRBC", quantity: 3, notes: "Hb 6.2, symptomatic anaemia");

        var request = BloodOrderHandler.NewRequest(order, BloodComponentType.PRBC);

        request.UnitsRequested.ShouldBe(3);
        request.ComponentType.ShouldBe(BloodComponentType.PRBC);
        request.OrderItemId.ShouldBe(order.OrderItemId);
        request.AdmissionId.ShouldBe(order.AdmissionId);
        request.Indication.ShouldBe("Hb 6.2, symptomatic anaemia");
        request.Status.ShouldBe(BloodRequestStatus.Pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Blood_order_without_a_valid_quantity_requests_one_unit(int quantity) =>
        BloodOrderHandler.NewRequest(Order(ServiceCategory.BloodBank, "BB-FFP", quantity), BloodComponentType.FFP).UnitsRequested.ShouldBe(1);

    [Theory]
    [InlineData(OrderPriority.Stat, BloodRequestUrgency.Emergency)]
    [InlineData(OrderPriority.Urgent, BloodRequestUrgency.Urgent)]
    [InlineData(OrderPriority.Routine, BloodRequestUrgency.Routine)]
    public void Order_priority_maps_to_blood_request_urgency(OrderPriority priority, BloodRequestUrgency urgency) =>
        BloodOrderHandler.NewRequest(Order(ServiceCategory.BloodBank, "BB-PRBC", priority: priority), BloodComponentType.PRBC).Urgency.ShouldBe(urgency);

    [Theory]
    [InlineData("BB-WB", BloodComponentType.WholeBlood)]
    [InlineData("bb-prbc", BloodComponentType.PRBC)]
    [InlineData("BB-PLT", BloodComponentType.PlateletConcentrate)]
    [InlineData("BB-CRYO", BloodComponentType.Cryoprecipitate)]
    public void Blood_service_codes_map_to_components(string code, BloodComponentType component) =>
        BloodBankServiceCodes.ComponentFor(code).ShouldBe(component);

    [Fact]
    public void Crossmatch_order_code_is_not_a_component_request() => BloodBankServiceCodes.ComponentFor("BB-CROSSMATCH").ShouldBeNull();

    [Fact]
    public void Physio_order_quantity_is_the_number_of_sessions_prescribed()
    {
        var order = Order(ServiceCategory.Physiotherapy, "PHY-IFT", quantity: 10, priority: OrderPriority.Urgent, notes: "Post TKR day 2");

        var referral = PhysioOrderHandler.NewReferral(order);

        referral.SessionsOrdered.ShouldBe(10);
        referral.Priority.ShouldBe(OrderPriority.Urgent);
        referral.Reason.ShouldBe("Post TKR day 2");
        referral.OrderItemId.ShouldBe(order.OrderItemId);
        referral.Status.ShouldBe(PhysioReferralStatus.New);
    }

    [Fact]
    public void Physio_order_without_notes_uses_the_service_name_as_reason() =>
        PhysioOrderHandler.NewReferral(Order(ServiceCategory.Physiotherapy, "PHY-SESSION")).Reason.ShouldBe("Service PHY-SESSION");

    [Fact]
    public void Treatment_plan_defaults_to_the_sessions_prescribed_on_the_order()
    {
        TreatmentPlan.SessionsToPlan(null, 10).Value.ShouldBe(10);
        TreatmentPlan.SessionsToPlan(12, 10).Value.ShouldBe(12, "the therapist's number wins");
        TreatmentPlan.SessionsToPlan(null, null).Error!.Fields!.ShouldContainKey("sessionsPlanned");
    }
}
