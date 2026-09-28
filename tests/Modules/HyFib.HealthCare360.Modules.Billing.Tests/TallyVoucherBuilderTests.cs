using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class TallyVoucherBuilderTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly TallyLedgerMap Ledgers = new() { IncomeByDepartment = new Dictionary<string, string> { ["LAB"] = "Laboratory Income" } };

    private static readonly TallySale InsuredSale = new(
        "NHOB260000001", Day, "Star Health", 490m, 1_000m, 0m,
        [new TallySaleLine("LAB", "Laboratory", 1_000m, 100m, 0m, 0m), new TallySaleLine("GS", "General Surgery", 500m, 0m, 45m, 45m)],
        IsCancellation: false);

    private static TallyDayBook Book(
        IReadOnlyList<TallySale>? sales = null,
        IReadOnlyList<TallyReceipt>? receipts = null,
        IReadOnlyList<TallyDisallowance>? disallowances = null,
        IReadOnlyList<TallyRefund>? refunds = null,
        IReadOnlyList<TallyCreditNote>? creditNotes = null,
        IReadOnlyList<TallyDoctorFee>? fees = null,
        IReadOnlyList<TallyPayout>? payouts = null) =>
        new(Day, sales ?? [], receipts ?? [], disallowances ?? [], refunds ?? [], creditNotes ?? [], fees ?? [], payouts ?? []);

    private static void ShouldBalance(AccountingVoucher voucher) =>
        voucher.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount), voucher.Number);

    private static AccountingEntry Entry(AccountingVoucher voucher, string ledger) => voucher.Entries.Single(e => e.LedgerName == ledger);

    [Fact]
    public void A_sales_voucher_balances_and_posts_income_by_department()
    {
        var voucher = TallyVoucherBuilder.Build(Book(sales: [InsuredSale]), Ledgers).Single();

        voucher.VoucherType.ShouldBe(TallyVoucherBuilder.SalesType);
        voucher.Number.ShouldBe("NHOB260000001");
        ShouldBalance(voucher);
        Entry(voucher, "Laboratory Income").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(1_000m), e => e.IsDebit.ShouldBeFalse(), e => e.CostCentre.ShouldBe("Laboratory"));
        Entry(voucher, "Hospital Service Income").Amount.ShouldBe(500m);
        Entry(voucher, "Discount Allowed").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(100m), e => e.IsDebit.ShouldBeTrue());
        Entry(voucher, "Patient Receivables").Amount.ShouldBe(490m);
        Entry(voucher, "Star Health").Amount.ShouldBe(1_000m);
        Entry(voucher, "Output CGST").Amount.ShouldBe(45m);
        Entry(voucher, "Output SGST").Amount.ShouldBe(45m);
        voucher.Narration.ShouldNotContain("Star Health");
    }

    [Fact]
    public void Round_off_is_posted_on_the_side_that_balances_the_bill()
    {
        var sale = new TallySale("NHOB260000002", Day, null, 100m, 0m, -0.40m, [new TallySaleLine(null, null, 100.40m, 0m, 0m, 0m)], false);

        var voucher = TallyVoucherBuilder.Build(Book(sales: [sale]), Ledgers).Single();

        ShouldBalance(voucher);
        Entry(voucher, "Round Off").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(0.40m), e => e.IsDebit.ShouldBeTrue());
    }

    [Fact]
    public void A_cancelled_bill_is_reversed_by_a_credit_note()
    {
        var voucher = TallyVoucherBuilder.Build(Book(sales: [InsuredSale with { IsCancellation = true }]), Ledgers).Single();

        voucher.VoucherType.ShouldBe(TallyVoucherBuilder.CreditNoteType);
        voucher.Number.ShouldBe("NHOB260000001/C");
        ShouldBalance(voucher);
        Entry(voucher, "Patient Receivables").IsDebit.ShouldBeFalse();
        Entry(voucher, "Laboratory Income").IsDebit.ShouldBeTrue();
    }

    [Fact]
    public void Receipts_debit_each_mode_and_credit_debtors_advances_or_the_payer()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(receipts:
        [
            new TallyReceipt("RC1", Day, ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Cash, 700m), new TallyPayment(PaymentMode.Upi, 300m)]),
            new TallyReceipt("RC2", Day, ReceiptPurpose.Deposit, null, [new TallyPayment(PaymentMode.Card, 5_000m)]),
            new TallyReceipt("RC3", Day, ReceiptPurpose.PayerSettlement, "Star Health", [new TallyPayment(PaymentMode.Insurance, 45_000m)]),
            new TallyReceipt("RC4", Day, ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Advance, 8_000m)]),
        ]), Ledgers);

        vouchers.ShouldAllBe(v => v.Entries.Where(e => e.IsDebit).Sum(e => e.Amount) == v.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
        var counter = vouchers.Single(v => v.Number == "RC1");
        counter.VoucherType.ShouldBe(TallyVoucherBuilder.ReceiptType);
        Entry(counter, "Cash").Amount.ShouldBe(700m);
        Entry(counter, "UPI Settlement Account").Amount.ShouldBe(300m);
        Entry(counter, "Patient Receivables").Amount.ShouldBe(1_000m);
        Entry(vouchers.Single(v => v.Number == "RC2"), "Patient Advances").IsDebit.ShouldBeFalse();
        Entry(vouchers.Single(v => v.Number == "RC3"), "Star Health").IsDebit.ShouldBeFalse();
        var adjustment = vouchers.Single(v => v.Number == "RC4");
        adjustment.VoucherType.ShouldBe(TallyVoucherBuilder.JournalType);
        Entry(adjustment, "Patient Advances").IsDebit.ShouldBeTrue();
    }

    [Fact]
    public void Refunds_pay_money_out_and_restored_advances_are_journals()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(refunds:
        [
            new TallyRefund("RF1", Day, RefundPurpose.Bill, PaymentMode.Cash, 300m),
            new TallyRefund("RF2", Day, RefundPurpose.Deposit, PaymentMode.NetBanking, 2_000m),
            new TallyRefund("RF3", Day, RefundPurpose.Bill, PaymentMode.Advance, 1_500m),
        ]), Ledgers);

        var cash = vouchers.Single(v => v.Number == "RF1");
        cash.VoucherType.ShouldBe(TallyVoucherBuilder.PaymentType);
        Entry(cash, "Cash").IsDebit.ShouldBeFalse();
        Entry(cash, "Patient Receivables").IsDebit.ShouldBeTrue();
        Entry(vouchers.Single(v => v.Number == "RF2"), "Patient Advances").IsDebit.ShouldBeTrue();
        var restored = vouchers.Single(v => v.Number == "RF3");
        restored.VoucherType.ShouldBe(TallyVoucherBuilder.JournalType);
        Entry(restored, "Patient Advances").IsDebit.ShouldBeFalse();
    }

    [Fact]
    public void An_online_prepayment_is_an_advance_and_its_refund_comes_out_of_advances()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(
            receipts: [new TallyReceipt("RC6", Day, ReceiptPurpose.Advance, null, [new TallyPayment(PaymentMode.PaymentGateway, 800m)])],
            refunds: [new TallyRefund("RF6", Day, RefundPurpose.Advance, PaymentMode.PaymentGateway, 800m)]), Ledgers);

        vouchers.ShouldAllBe(v => v.Entries.Where(e => e.IsDebit).Sum(e => e.Amount) == v.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
        var receipt = vouchers.Single(v => v.Number == "RC6");
        receipt.VoucherType.ShouldBe(TallyVoucherBuilder.ReceiptType);
        Entry(receipt, "Patient Advances").IsDebit.ShouldBeFalse();
        var refund = vouchers.Single(v => v.Number == "RF6");
        refund.VoucherType.ShouldBe(TallyVoucherBuilder.PaymentType);
        Entry(refund, "Patient Advances").IsDebit.ShouldBeTrue();
    }

    [Fact]
    public void A_bill_with_returns_posts_net_income_and_still_balances()
    {
        var sale = new TallySale(
            "NHIB260000009", Day, null, 830m, 0m, 0m,
            [new TallySaleLine("PHARM", "Pharmacy", 1_000m, 0m, 0m, 0m), new TallySaleLine("PHARM", "Pharmacy", -170m, 0m, 0m, 0m)],
            IsCancellation: false);

        var voucher = TallyVoucherBuilder.Build(Book(sales: [sale]), Ledgers).Single();

        ShouldBalance(voucher);
        Entry(voucher, "Hospital Service Income").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(830m), e => e.IsDebit.ShouldBeFalse());
    }

    [Fact]
    public void Credit_notes_write_offs_and_claim_deductions_use_their_own_ledgers()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(
            creditNotes: [new TallyCreditNote("CN1", Day, CreditNoteKind.CreditNote, 200m), new TallyCreditNote("CN2", Day, CreditNoteKind.WriteOff, 750m)],
            disallowances: [new TallyDisallowance("NHCL26000001/ab12cd", Day, "Star Health", 3_000m)]), Ledgers);

        Entry(vouchers.Single(v => v.Number == "CN1"), "Sales Returns & Allowances").IsDebit.ShouldBeTrue();
        Entry(vouchers.Single(v => v.Number == "CN2"), "Bad Debts Written Off").Amount.ShouldBe(750m);
        var deduction = vouchers.Single(v => v.Number == "NHCL26000001/ab12cd");
        Entry(deduction, "Insurance Disallowances").IsDebit.ShouldBeTrue();
        Entry(deduction, "Star Health").IsDebit.ShouldBeFalse();
    }

    [Fact]
    public void Doctor_fees_are_journalised_per_doctor_and_reversals_flip_sides()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(fees: [new TallyDoctorFee("Dr. Arjun Mehta", 1_200m), new TallyDoctorFee("Dr. Kavya Nair", -400m)]), Ledgers);

        var accrual = vouchers[0];
        accrual.VoucherType.ShouldBe(TallyVoucherBuilder.JournalType);
        Entry(accrual, "Doctor Professional Fees").IsDebit.ShouldBeTrue();
        Entry(accrual, "Doctor Fees Payable - Dr. Arjun Mehta").Amount.ShouldBe(1_200m);
        Entry(vouchers[1], "Doctor Fees Payable - Dr. Kavya Nair").IsDebit.ShouldBeTrue();
        vouchers.Select(v => v.Number).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void A_payout_debits_the_doctor_and_credits_tds_recoveries_and_bank()
    {
        var voucher = TallyVoucherBuilder.Build(Book(payouts: [new TallyPayout("NHDP2600001", Day, "Dr. Arjun Mehta", 50_000m, 5_000m, 2_000m, 43_000m)]), Ledgers).Single();

        ShouldBalance(voucher);
        Entry(voucher, "Doctor Fees Payable - Dr. Arjun Mehta").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(50_000m), e => e.IsDebit.ShouldBeTrue());
        Entry(voucher, "TDS Payable (194J)").Amount.ShouldBe(5_000m);
        Entry(voucher, "Doctor Recoveries").Amount.ShouldBe(2_000m);
        Entry(voucher, "Bank").Amount.ShouldBe(43_000m);
    }

    [Fact]
    public void Ledger_mapping_from_settings_json_is_case_insensitive_with_defaults_for_the_rest()
    {
        var map = JsonSerializer.Deserialize<TallyLedgerMap>(
            """{"defaultIncome":"Income - Hospital","incomeByDepartment":{"lab":"Lab Income"},"modeLedgers":{"card":"HDFC Card Settlement"}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        map.IncomeFor("LAB").ShouldBe("Lab Income");
        map.IncomeFor("RAD").ShouldBe("Income - Hospital");
        map.ModeLedger(PaymentMode.Card).ShouldBe("HDFC Card Settlement");
        map.ModeLedger(PaymentMode.Cash).ShouldBe("Cash");
        map.PatientDebtors.ShouldBe("Patient Receivables");
    }

    [Fact]
    public void A_quiet_day_produces_no_vouchers()
    {
        Book().IsEmpty.ShouldBeTrue();
        TallyVoucherBuilder.Build(Book(), Ledgers).ShouldBeEmpty();
    }
}
