using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// Which unbilled charges the billing desk cancels on its own and which wait for someone who approves discounts
/// (maker-checker, slice 6): an OT, procedure, implant or bed-day charge always waits; any other waits above the
/// hospital's amount; a credit (a return) cancelled makes the patient owe more, so it never waits.
/// </summary>
public sealed class ChargeCancelRuleTests
{
    [Theory]
    [InlineData(ServiceCategory.Surgery)]
    [InlineData(ServiceCategory.Procedure)]
    [InlineData(ServiceCategory.BedCharge)]
    public void An_ot_procedure_or_bed_day_charge_always_waits_however_small(ServiceCategory category)
    {
        ChargeService.CancelNeedsApproval(TestData.Charge(10m, category: category), approvalAbove: 1_000m).ShouldBeTrue();
    }

    [Fact]
    public void An_implant_always_waits_by_its_device_or_its_service()
    {
        var device = TestData.Charge(50m, category: ServiceCategory.Consumable);
        device.Device = new DeviceDetails("Xience", "Abbott", "Drug-eluting stent", "B1", null, 50m, null);
        var byCode = TestData.Charge(50m, category: ServiceCategory.Consumable);
        byCode.ServiceCode = "OT-IMPLANT";

        ChargeService.CancelNeedsApproval(device, 1_000m).ShouldBeTrue();
        ChargeService.CancelNeedsApproval(byCode, 1_000m).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(1_000.01, true)]
    [InlineData(0, false)]
    public void Any_other_charge_waits_only_above_the_hospitals_amount(double net, bool waits)
    {
        var charge = TestData.Charge((decimal)net, category: ServiceCategory.Laboratory);

        ChargeService.CancelNeedsApproval(charge, approvalAbove: 1_000m).ShouldBe(waits);
    }

    [Fact]
    public void At_zero_every_charge_worth_anything_waits_and_a_negative_amount_acts_as_zero()
    {
        ChargeService.CancelNeedsApproval(TestData.Charge(1m, category: ServiceCategory.Consumable), 0m).ShouldBeTrue();
        ChargeService.CancelNeedsApproval(TestData.Charge(1m, category: ServiceCategory.Consumable), -50m).ShouldBeTrue();
    }

    [Fact]
    public void A_credit_cancelled_never_waits()
    {
        var credit = TestData.Charge(5_000m, quantity: -1m, category: ServiceCategory.Procedure);

        credit.IsCredit.ShouldBeTrue();
        ChargeService.CancelNeedsApproval(credit, 0m).ShouldBeFalse();
    }
}
