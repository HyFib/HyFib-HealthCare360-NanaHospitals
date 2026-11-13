using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>Card and UPI payments taken on the hospital's terminal: what a request may name, and what the invoice keeps.</summary>
public sealed class TerminalPaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 5, 0, 0, TimeSpan.Zero);
    private static readonly Guid Terminal = Guid.CreateVersion7();

    private static PharmacySale Sale(params SalePayment[] payments)
    {
        var sale = new PharmacySale { InvoiceNumber = "NHPH260000102", SoldAt = Now, PatientId = Guid.CreateVersion7() };
        var item = new PharmacySaleItem { ItemName = "Amoxicillin 500 mg", BatchNumber = "AX1", UnitMrp = 45m, Quantity = 1, GstRate = 12 };
        item.Price(0);
        sale.Items.Add(item);
        sale.Complete(payments, creditAllowed: true, Now, Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        return sale;
    }

    [Theory]
    [InlineData(PaymentMode.Card)]
    [InlineData(PaymentMode.Upi)]
    public void A_terminal_payment_needs_no_typed_reference(PaymentMode mode) =>
        new SalePaymentValidator().Validate(new SalePayment(mode, 45m, null, Terminal)).IsValid.ShouldBeTrue();

    [Fact]
    public void A_card_payment_keyed_in_still_needs_its_reference() =>
        new SalePaymentValidator().Validate(new SalePayment(PaymentMode.Card, 45m, null)).IsValid.ShouldBeFalse();

    [Theory]
    [InlineData(PaymentMode.Cash)]
    [InlineData(PaymentMode.Credit)]
    public void Only_a_card_or_UPI_payment_is_taken_on_the_terminal(PaymentMode mode)
    {
        var result = new SalePaymentValidator().Validate(new SalePayment(mode, 45m, null, Terminal));

        result.Errors.ShouldContain(e => e.ErrorMessage == "Only a card or UPI payment is taken on the terminal.");
    }

    [Fact]
    public void A_terminal_payment_is_recorded_once_in_a_request()
    {
        SalePaymentValidator.EachTerminalSaleOnce([new SalePayment(PaymentMode.Card, 20m, null, Terminal), new SalePayment(PaymentMode.Card, 25m, null, Terminal)]).ShouldBeFalse();
        SalePaymentValidator.EachTerminalSaleOnce([new SalePayment(PaymentMode.Card, 20m, null, Terminal), new SalePayment(PaymentMode.Upi, 25m, null, Guid.CreateVersion7())]).ShouldBeTrue();
        SalePaymentValidator.EachTerminalSaleOnce([new SalePayment(PaymentMode.Cash, 20m, null), new SalePayment(PaymentMode.Cash, 25m, null)]).ShouldBeTrue();
    }

    [Fact]
    public void A_balance_collected_on_the_terminal_is_only_card_or_UPI()
    {
        var validator = new CollectPaymentRequestValidator();

        validator.Validate(new CollectPaymentRequest(PaymentMode.Card, 10m, null, Terminal)).IsValid.ShouldBeTrue();
        validator.Validate(new CollectPaymentRequest(PaymentMode.Cash, 10m, null, Terminal)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Completing_an_invoice_keeps_the_terminal_sale_of_each_payment_and_a_refund_does_not()
    {
        var sale = Sale(new SalePayment(PaymentMode.Card, 45m, "627910123456", Terminal));
        sale.Payments.Single().TerminalSaleId.ShouldBe(Terminal);

        sale.Cancel("Billed to the wrong customer", new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 7), Now.AddMinutes(10), Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();

        sale.Payments.Single(p => p.IsRefund).TerminalSaleId.ShouldBeNull();
    }

    [Fact]
    public void A_collected_balance_keeps_its_terminal_sale()
    {
        var sale = Sale(new SalePayment(PaymentMode.Credit, 45m, null));

        sale.CollectPayment(PaymentMode.Upi, 45m, null, Now.AddDays(1), null, Terminal).IsSuccess.ShouldBeTrue();

        sale.Payments.Single(p => p.Mode == PaymentMode.Upi).TerminalSaleId.ShouldBe(Terminal);
    }
}
