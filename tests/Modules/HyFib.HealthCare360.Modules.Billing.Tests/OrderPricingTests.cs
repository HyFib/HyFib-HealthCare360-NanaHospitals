using HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>Which clinical orders Billing prices itself, so no service is charged twice.</summary>
public sealed class OrderPricingTests
{
    private static ServiceOrderPlaced Order(ServiceCategory category, Guid? packageBookingId = null, int quantity = 1) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "NHOR260000001", TestData.PatientId, Guid.CreateVersion7(), null, TestData.FacilityId,
        Guid.CreateVersion7(), "PROC-DRESS", "Dressing - minor", category, OrderPriority.Routine, Guid.CreateVersion7(), null, TestData.Now,
        packageBookingId, quantity);

    [Fact]
    public void Procedures_are_priced_when_ordered()
    {
        OrderPricing.IsBilledOnOrder(Order(ServiceCategory.Procedure)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ServiceCategory.Laboratory)]
    [InlineData(ServiceCategory.Radiology)]
    [InlineData(ServiceCategory.Cardiology)]
    [InlineData(ServiceCategory.Surgery)]
    [InlineData(ServiceCategory.Physiotherapy)]
    [InlineData(ServiceCategory.Dialysis)]
    [InlineData(ServiceCategory.BloodBank)]
    [InlineData(ServiceCategory.Dietary)]
    [InlineData(ServiceCategory.Consultation)]
    public void Services_another_module_charges_are_never_priced_on_order(ServiceCategory category)
    {
        OrderPricing.IsBilledOnOrder(Order(category)).ShouldBeFalse();
    }

    [Fact]
    public void Health_checkup_components_are_billed_under_the_package_only()
    {
        OrderPricing.IsBilledOnOrder(Order(ServiceCategory.Procedure, packageBookingId: Guid.CreateVersion7())).ShouldBeFalse();
    }
}
