using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>Online appointment prepayments held as patient advances (OA-03).</summary>
public sealed class PatientAdvanceTests
{
    private static PatientAdvance Prepaid(decimal amount = 800m) => new()
    {
        FacilityId = TestData.FacilityId,
        PatientId = TestData.PatientId,
        ReceiptNumber = "NHRC260000123",
        GatewayOrderId = "order_Q9zY81",
        GatewayPaymentId = "pay_Nx12AbC",
        Amount = amount,
        ReceivedAt = TestData.Now,
    };

    [Fact]
    public void Adjustments_and_refunds_draw_down_the_balance()
    {
        var advance = Prepaid();

        advance.Adjust(500m).IsSuccess.ShouldBeTrue();
        advance.Refund(300m).IsSuccess.ShouldBeTrue();

        advance.Balance.ShouldBe(0m);
        advance.AdjustedAmount.ShouldBe(500m);
        advance.RefundedAmount.ShouldBe(300m);
    }

    [Fact]
    public void An_advance_cannot_be_used_beyond_its_balance()
    {
        var advance = Prepaid();
        advance.Adjust(600m);

        advance.Refund(200.01m).Error!.Code.ShouldBe("billing.advance_insufficient");
        advance.Adjust(0m).Error!.Code.ShouldBe("billing.amount_invalid");
        advance.Balance.ShouldBe(200m);
    }

    [Fact]
    public void A_cancelled_bill_gives_its_adjustment_back_but_never_more()
    {
        var advance = Prepaid();
        advance.Adjust(800m);

        advance.ReverseAdjustment(500m);
        advance.Balance.ShouldBe(500m);

        advance.ReverseAdjustment(1_000m);
        advance.AdjustedAmount.ShouldBe(0m);
        advance.Balance.ShouldBe(800m);
    }

    [Fact]
    public void Receipting_an_online_prepayment_notifies_other_modules()
    {
        var receipt = new Receipt { ReceiptNumber = "NHRC260000123", PatientId = TestData.PatientId, Purpose = ReceiptPurpose.Advance };
        receipt.AddPayment(new PaymentLineData(PaymentMode.PaymentGateway, 800m, Reference: "order_Q9zY81", ExternalId: "pay_Nx12AbC"));

        receipt.Record(TestData.Now).IsSuccess.ShouldBeTrue();

        var raised = receipt.DomainEvents.OfType<PaymentReceived>().Single();
        raised.Amount.ShouldBe(800m);
        raised.BillId.ShouldBeNull();
    }

    [Fact]
    public void Adjusting_an_advance_moves_no_money_so_it_raises_no_payment()
    {
        var adjustment = new Receipt { Purpose = ReceiptPurpose.BillPayment };
        adjustment.AddPayment(new PaymentLineData(PaymentMode.Advance, 800m, Reference: "Advance NHRC260000123"));

        adjustment.Record(TestData.Now);

        adjustment.DomainEvents.ShouldBeEmpty();
    }
}
