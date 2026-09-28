using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class PaymentLineRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static bool IsValid(PaymentLineData line, bool atCounter = true) => PaymentLineRules.Validate(line, Today, atCounter) is null;

    [Fact]
    public void Cash_needs_no_reference()
    {
        IsValid(new PaymentLineData(PaymentMode.Cash, 500m)).ShouldBeTrue();
    }

    [Fact]
    public void Card_needs_the_last_four_digits_unless_a_terminal_captured_it()
    {
        IsValid(new PaymentLineData(PaymentMode.Card, 500m)).ShouldBeFalse();
        IsValid(new PaymentLineData(PaymentMode.Card, 500m, CardLast4: "12a4")).ShouldBeFalse();
        IsValid(new PaymentLineData(PaymentMode.Card, 500m, CardLast4: "1234")).ShouldBeTrue();
        IsValid(new PaymentLineData(PaymentMode.Card, 500m, ExternalId: "POS-TXN-1")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(PaymentMode.Upi)]
    [InlineData(PaymentMode.NetBanking)]
    [InlineData(PaymentMode.Wallet)]
    public void Electronic_transfers_need_a_reference(PaymentMode mode)
    {
        IsValid(new PaymentLineData(mode, 500m)).ShouldBeFalse();
        IsValid(new PaymentLineData(mode, 500m, Reference: "425612345678")).ShouldBeTrue();
    }

    [Fact]
    public void Cheques_need_number_bank_and_a_date_within_three_months_and_not_post_dated()
    {
        PaymentLineData Cheque(string? number = "123456", string? bank = "SBI", DateOnly? date = null) =>
            new(PaymentMode.Cheque, 5_000m, ChequeNumber: number, ChequeBank: bank, ChequeDate: date ?? Today);

        IsValid(Cheque()).ShouldBeTrue();
        IsValid(Cheque(number: "12345")).ShouldBeFalse();
        IsValid(Cheque(bank: null)).ShouldBeFalse();
        IsValid(new PaymentLineData(PaymentMode.Cheque, 5_000m, ChequeNumber: "123456", ChequeBank: "SBI")).ShouldBeFalse();
        IsValid(Cheque(date: Today.AddDays(1))).ShouldBeFalse();
        IsValid(Cheque(date: Today.AddMonths(-3))).ShouldBeTrue();
        IsValid(Cheque(date: Today.AddMonths(-3).AddDays(-1))).ShouldBeFalse();
    }

    [Theory]
    [InlineData(PaymentMode.Advance)]
    [InlineData(PaymentMode.Insurance)]
    public void Advance_adjustments_and_insurer_settlements_are_not_counter_payments(PaymentMode mode)
    {
        IsValid(new PaymentLineData(mode, 500m, Reference: "REF")).ShouldBeFalse();
        IsValid(new PaymentLineData(mode, 500m, Reference: "REF"), atCounter: false).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(10.005)]
    public void Amounts_must_be_positive_rupees_with_at_most_two_decimals(decimal amount)
    {
        IsValid(new PaymentLineData(PaymentMode.Cash, amount)).ShouldBeFalse();
    }

    [Fact]
    public void A_split_receipt_totals_its_modes_and_notifies_other_modules()
    {
        var receipt = new Receipt { ReceiptNumber = "NHRC260000001", PatientId = TestData.PatientId, Purpose = ReceiptPurpose.BillPayment };
        receipt.AddPayment(new PaymentLineData(PaymentMode.Cash, 700m));
        receipt.AddPayment(new PaymentLineData(PaymentMode.Upi, 300.50m, Reference: "425612345678"));

        receipt.Record(TestData.Now).IsSuccess.ShouldBeTrue();

        receipt.Amount.ShouldBe(1_000.50m);
        receipt.ModeSummary.ShouldBe("Cash + Upi");
        var raised = receipt.DomainEvents.OfType<PaymentReceived>().Single();
        raised.Amount.ShouldBe(1_000.50m);
        raised.Mode.ShouldBe("Cash + Upi");
    }

    [Fact]
    public void Advance_adjustments_and_payer_settlements_do_not_raise_payment_received()
    {
        var adjustment = new Receipt { Purpose = ReceiptPurpose.BillPayment };
        adjustment.AddPayment(new PaymentLineData(PaymentMode.Advance, 5_000m));
        var settlement = new Receipt { Purpose = ReceiptPurpose.PayerSettlement };
        settlement.AddPayment(new PaymentLineData(PaymentMode.Insurance, 45_000m, Reference: "UTR1"));

        adjustment.Record(TestData.Now);
        settlement.Record(TestData.Now);

        adjustment.DomainEvents.ShouldBeEmpty();
        settlement.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_receipt_cannot_be_recorded()
    {
        new Receipt().Record(TestData.Now).IsFailure.ShouldBeTrue();
    }
}
