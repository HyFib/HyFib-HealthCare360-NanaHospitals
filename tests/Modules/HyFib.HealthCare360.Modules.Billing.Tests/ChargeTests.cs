using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class ChargeTests
{
    [Fact]
    public void Price_is_rounded_per_line_with_discount_before_gst_and_an_equal_cgst_sgst_split()
    {
        var charge = TestData.Charge(99.99m, quantity: 3m, taxRate: 18m, tariffDiscountPercent: 10m);

        charge.GrossAmount.ShouldBe(299.97m);
        charge.DiscountAmount.ShouldBe(30.00m);
        charge.TaxAmount.ShouldBe(48.59m);
        charge.CgstAmount.ShouldBe(24.30m);
        charge.SgstAmount.ShouldBe(24.29m);
        charge.NetAmount.ShouldBe(318.56m);
    }

    [Fact]
    public void Tariff_discount_is_ignored_for_services_that_cannot_be_discounted()
    {
        var charge = TestData.Charge(500m, discountAllowed: false, tariffDiscountPercent: 20m);

        charge.DiscountPercent.ShouldBe(0m);
        charge.DiscountAmount.ShouldBe(0m);
        charge.NetAmount.ShouldBe(500m);
    }

    [Fact]
    public void A_charge_can_be_reserved_by_only_one_bill()
    {
        var charge = TestData.Charge(100m);

        charge.ReserveFor(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        charge.ReserveFor(Guid.CreateVersion7()).Error!.Code.ShouldBe("billing.charge_unavailable");
    }

    [Fact]
    public void Released_charge_returns_to_the_unbilled_pool()
    {
        var charge = TestData.Charge(100m);
        charge.ReserveFor(Guid.CreateVersion7());
        charge.MarkBilled();

        charge.Release();

        charge.Status.ShouldBe(ChargeStatus.Unbilled);
        charge.IsAvailableForBilling.ShouldBeTrue();
    }

    [Fact]
    public void Billed_or_reserved_charges_cannot_be_cancelled_directly()
    {
        var reserved = TestData.Charge(100m);
        reserved.ReserveFor(Guid.CreateVersion7());
        var billed = TestData.Charge(100m);
        billed.ReserveFor(Guid.CreateVersion7());
        billed.MarkBilled();

        reserved.Cancel("x", TestData.Now).Error!.Code.ShouldBe("billing.charge_on_draft");
        billed.Cancel("x", TestData.Now).Error!.Code.ShouldBe("billing.charge_billed");
    }

    [Fact]
    public void Cancelling_is_idempotent()
    {
        var charge = TestData.Charge(100m);

        charge.Cancel("Order withdrawn", TestData.Now).IsSuccess.ShouldBeTrue();
        charge.Cancel("Again", TestData.Now).IsSuccess.ShouldBeTrue();
        charge.Status.ShouldBe(ChargeStatus.Cancelled);
        charge.CancelReason.ShouldBe("Order withdrawn");
    }

    [Theory]
    [InlineData(ChargeStatus.Cancelled, null, ChargeBillingState.Cancelled)]
    [InlineData(ChargeStatus.Unbilled, null, ChargeBillingState.Unbilled)]
    [InlineData(ChargeStatus.Unbilled, BillStatus.Draft, ChargeBillingState.Unbilled)]
    [InlineData(ChargeStatus.Billed, BillStatus.Finalized, ChargeBillingState.Billed)]
    [InlineData(ChargeStatus.Billed, BillStatus.PartiallyPaid, ChargeBillingState.Billed)]
    [InlineData(ChargeStatus.Billed, BillStatus.Paid, ChargeBillingState.Paid)]
    public void Billing_state_reported_to_other_modules_is_paid_only_when_the_bill_is_paid(ChargeStatus status, BillStatus? billStatus, ChargeBillingState expected)
    {
        BillingStatusProvider.ToState(status, billStatus).ShouldBe(expected);
    }
}
