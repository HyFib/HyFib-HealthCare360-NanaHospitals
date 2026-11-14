using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>A canteen bill paid by card or UPI on the hospital's terminal.</summary>
public sealed class CanteenTerminalPaymentTests
{
    private static CreateSaleRequest Request(CanteenPaymentMode mode, Guid? terminalSaleId, string? reference = null) =>
        new(CanteenCustomerType.Visitor, null, null, null, null, mode, reference, [new SaleLineInput(Guid.NewGuid(), 1)], terminalSaleId);

    [Theory]
    [InlineData(CanteenPaymentMode.Card)]
    [InlineData(CanteenPaymentMode.Upi)]
    public void A_card_or_UPI_bill_can_name_a_terminal_sale(CanteenPaymentMode mode) =>
        new CreateSaleRequestValidator().Validate(Request(mode, Guid.NewGuid())).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(CanteenPaymentMode.Cash)]
    [InlineData(CanteenPaymentMode.StaffCredit)]
    public void A_bill_paid_any_other_way_cannot_name_a_terminal_sale(CanteenPaymentMode mode)
    {
        var result = new CreateSaleRequestValidator().Validate(Request(mode, Guid.NewGuid()));

        result.Errors.ShouldContain(e => e.ErrorMessage == "Only a card or UPI payment is taken on the terminal.");
    }
}
