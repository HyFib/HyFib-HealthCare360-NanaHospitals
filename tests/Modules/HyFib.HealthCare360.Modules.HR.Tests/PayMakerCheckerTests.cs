using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Tests;

/// <summary>
/// The pure rules of maker-checker slice 11: which changes to pay and leave wait for a second person, how payment details
/// read in words (never more than an account's last four digits), and when a payroll's payment details are fixed.
/// </summary>
public sealed class PayMakerCheckerTests
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Theory]
    [InlineData(PayrollInputKind.Earning, true)]
    [InlineData(PayrollInputKind.LopDaysOverride, true)]
    [InlineData(PayrollInputKind.TdsOverride, true)]
    [InlineData(PayrollInputKind.Deduction, false)]
    public void What_raises_pay_waits_and_a_deduction_does_not(PayrollInputKind kind, bool waits) =>
        PayChangeRule.InputWaits(kind).ShouldBe(waits);

    [Fact]
    public void A_deduction_another_module_posted_waits_to_be_removed_even_by_whoever_posted_it()
    {
        PayChangeRule.RemovalWaits(PayrollInputKind.Deduction, PayrollInputSource.Integration, Maker, Maker).ShouldBeTrue();
        PayChangeRule.RemovalWaits(PayrollInputKind.Deduction, PayrollInputSource.Integration, null, Maker).ShouldBeTrue();
    }

    [Fact]
    public void A_deduction_another_person_entered_waits_and_ones_own_is_taken_back_at_once()
    {
        PayChangeRule.RemovalWaits(PayrollInputKind.Deduction, PayrollInputSource.Manual, Other, Maker).ShouldBeTrue();
        PayChangeRule.RemovalWaits(PayrollInputKind.Deduction, PayrollInputSource.Import, Other, Maker).ShouldBeTrue();
        PayChangeRule.RemovalWaits(PayrollInputKind.Deduction, PayrollInputSource.Manual, Maker, Maker).ShouldBeFalse();
    }

    [Theory]
    [InlineData(PayrollInputKind.Earning)]
    [InlineData(PayrollInputKind.LopDaysOverride)]
    [InlineData(PayrollInputKind.TdsOverride)]
    public void An_earning_or_override_is_removed_at_once(PayrollInputKind kind) =>
        PayChangeRule.RemovalWaits(kind, PayrollInputSource.Manual, Other, Maker).ShouldBeFalse();

    [Theory]
    [InlineData(1.5, true)]
    [InlineData(0.5, true)]
    [InlineData(-2, false)]
    public void A_leave_credit_waits_and_a_debit_does_not(decimal days, bool waits) => LeaveAdjustmentRule.Waits(days).ShouldBe(waits);

    [Fact]
    public void Payment_details_read_with_the_last_four_digits_only()
    {
        var bank = new PaymentDetails(SalaryPaymentMode.BankTransfer, "State Bank of India", "v1.cipher", HrIdentifiers.MaskAccount("001234567890"), "v1.ifsc",
            HrIdentifiers.MaskIfsc("SBIN0001234"));
        var cash = new PaymentDetails(SalaryPaymentMode.Cash, null, null, null, null, null);

        var words = PaymentDetailsChange.Describe(bank, cash);

        words.ShouldBe("bank transfer to account XXXXXXXX7890 (SBIN0XXXXXX, State Bank of India) → cash");
        words.ShouldNotContain("001234567890");
        words.ShouldNotContain("12345");
        PaymentDetailsChange.Describe(cash, cash).ShouldBe("unchanged");
        new PaymentDetails(SalaryPaymentMode.BankTransfer, null, null, null, null, null).Words.ShouldBe("bank transfer, no account on record");
    }

    [Fact]
    public void Payment_details_are_the_same_when_the_same_account_is_encrypted_again()
    {
        // Two encryptions of one account differ as text; what they decrypt to is compared.
        var plain = new Dictionary<string, string> { ["v1.a"] = "001234567890", ["v2.a"] = "001234567890", ["v1.b"] = "009876543210", ["v1.i"] = "SBIN0001234" };
        string? Unprotect(string? cipher) => cipher is null ? null : plain[cipher];
        var before = new PaymentDetails(SalaryPaymentMode.BankTransfer, "SBI", "v1.a", "XXXXXXXX7890", "v1.i", "SBIN0XXXXXX");

        PaymentDetailsChange.Same(before, before with { AccountCipher = "v2.a" }, Unprotect).ShouldBeTrue();
        PaymentDetailsChange.Same(before, before with { AccountCipher = "v1.b" }, Unprotect).ShouldBeFalse();
        PaymentDetailsChange.Same(before, before with { Mode = SalaryPaymentMode.Cash }, Unprotect).ShouldBeFalse();
        PaymentDetailsChange.Same(before, before with { BankName = "HDFC" }, Unprotect).ShouldBeFalse();
    }

    [Fact]
    public void A_payroll_fixes_its_payment_details_when_approved_and_lets_them_go_when_reopened()
    {
        var now = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);
        var run = new PayrollRun { Year = 2026, Month = 9 };
        run.MarkProcessed(Maker, now, 1, 10_000, 0, 10_000).IsSuccess.ShouldBeTrue();
        run.BankDetailsFixedAt.ShouldBeNull();

        run.Approve(Other, now).IsSuccess.ShouldBeTrue();
        run.BankDetailsFixedAt.ShouldBe(now);

        run.Reopen("Arrears missed").IsSuccess.ShouldBeTrue();
        run.BankDetailsFixedAt.ShouldBeNull();
    }

    [Fact]
    public void A_payment_details_change_needs_a_reason_the_version_seen_and_the_account_with_its_ifsc()
    {
        var validator = new UpdateBankDetailsRequestValidator();

        validator.Validate(new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, "SBI", "001234567890", "SBIN0001234", false, "Moved banks", 3)).IsValid.ShouldBeTrue();
        validator.Validate(new UpdateBankDetailsRequest(SalaryPaymentMode.Cash, null, null, null, false, "No account yet", 3)).IsValid.ShouldBeTrue();
        validator.Validate(new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, null, "001234567890", "SBIN0001234", false, " ", 3)).Errors
            .ShouldContain(e => e.PropertyName == "Reason");
        validator.Validate(new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, null, "001234567890", null, false, "Moved", 3)).Errors
            .ShouldContain(e => e.PropertyName == "Ifsc");
        validator.Validate(new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, null, "001234567890", "SBIN0001234", false, "Moved", 0)).Errors
            .ShouldContain(e => e.PropertyName == "Version");
    }
}
