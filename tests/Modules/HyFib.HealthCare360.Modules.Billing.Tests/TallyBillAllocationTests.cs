using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// Bill references on payer and supplier ledgers (T14: "Yes"), so their outstanding shows bill by bill in Tally: a sale's
/// payer share is a new bill, what settles or reduces it is set against it, and what HC360 cannot tie to a bill stands
/// on account. A line's allocations always add up to the line.
/// </summary>
public sealed class TallyBillAllocationTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly TallyLedgerMap Ledgers = new();

    private static readonly TallySale InsuredSale = new(
        "NHOB260000001", Day, "Star Health", 490m, 1_000m, 0m,
        [new TallySaleLine("LAB", "Laboratory", 1_000m, 100m, 0m, 0m), new TallySaleLine("GS", "General Surgery", 500m, 0m, 45m, 45m)],
        IsCancellation: false);

    private static TallyDayBook Book(
        IReadOnlyList<TallySale>? sales = null,
        IReadOnlyList<TallyReceipt>? receipts = null,
        IReadOnlyList<TallyDisallowance>? disallowances = null,
        IReadOnlyList<TallyCreditNote>? creditNotes = null,
        VendorAccountsDay? vendors = null) =>
        new(Day, sales ?? [], receipts ?? [], disallowances ?? [], [], creditNotes ?? [], [], []) { Vendors = vendors ?? VendorAccountsDay.None };

    private static AccountingEntry Entry(AccountingVoucher voucher, string ledger) => voucher.Entries.Single(e => e.LedgerName == ledger);

    private static void ShouldAddUp(AccountingVoucher voucher)
    {
        foreach (var entry in voucher.Entries.Where(e => e.BillAllocations.Count > 0))
        {
            entry.BillAllocations.Sum(b => b.Amount).ShouldBe(entry.Amount, $"{voucher.Number}: {entry.LedgerName}");
        }
    }

    [Fact]
    public void A_sales_payer_share_is_a_new_bill_of_the_payer_and_the_patients_share_has_no_bill()
    {
        var voucher = TallyVoucherBuilder.Build(Book(sales: [InsuredSale]), Ledgers).Single();

        Entry(voucher, "Star Health").BillAllocations.ShouldBe([new AccountingBillAllocation("NHOB260000001", AccountingBillType.NewRef, 1_000m)]);
        Entry(voucher, "Patient Receivables").BillAllocations.ShouldBeEmpty();
        voucher.Entries.Where(e => e.LedgerName != "Star Health").ShouldAllBe(e => e.BillAllocations.Count == 0);
    }

    [Fact]
    public void A_cancelled_bill_is_taken_off_the_payers_bill()
    {
        var voucher = TallyVoucherBuilder.Build(Book(sales: [InsuredSale with { IsCancellation = true }]), Ledgers).Single();

        var payer = Entry(voucher, "Star Health");
        payer.IsDebit.ShouldBeFalse();
        payer.BillAllocations.ShouldBe([new AccountingBillAllocation("NHOB260000001", AccountingBillType.AgainstRef, 1_000m)]);
    }

    [Fact]
    public void An_insurers_settlement_and_its_deductions_are_set_against_the_bill_they_settle()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(
            receipts:
            [
                new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.PayerSettlement, "Star Health", [new TallyPayment(PaymentMode.Insurance, 900m)])
                {
                    BillNumber = "NHOB260000001",
                },
            ],
            disallowances: [new TallyDisallowance("NHCL26000001/0198a1", Day, "Star Health", 100m) { BillNumber = "NHOB260000001" }]), Ledgers);

        Entry(vouchers.Single(v => v.Number == "NHRC260000001"), "Star Health").BillAllocations
            .ShouldBe([new AccountingBillAllocation("NHOB260000001", AccountingBillType.AgainstRef, 900m)]);
        Entry(vouchers.Single(v => v.Number == "NHCL26000001/0198a1"), "Star Health").BillAllocations
            .ShouldBe([new AccountingBillAllocation("NHOB260000001", AccountingBillType.AgainstRef, 100m)]);
        vouchers.SelectMany(v => v.Entries).Where(e => e.LedgerName != "Star Health").ShouldAllBe(e => e.BillAllocations.Count == 0);
    }

    [Fact]
    public void A_settlement_whose_bill_is_not_known_stands_on_account()
    {
        var voucher = TallyVoucherBuilder.Build(Book(receipts:
            [new TallyReceipt("NHRC260000002", Day, ReceiptPurpose.PayerSettlement, "Star Health", [new TallyPayment(PaymentMode.Insurance, 900m)])]), Ledgers).Single();

        Entry(voucher, "Star Health").BillAllocations.ShouldBe([new AccountingBillAllocation(null, AccountingBillType.OnAccount, 900m)]);
    }

    [Fact]
    public void A_payer_credit_note_does_not_touch_the_payers_ledger_as_the_deduction_already_reduced_its_bill()
    {
        var voucher = TallyVoucherBuilder.Build(Book(creditNotes: [new TallyCreditNote("NHCN260000001", Day, CreditNoteKind.PayerShare, 118m, 9m, 9m)]), Ledgers)
            .Single();

        voucher.Entries.Select(e => e.LedgerName).ShouldBe(["Sales Returns & Allowances", "Output CGST", "Output SGST", "Insurance Disallowances"], ignoreOrder: true);
        voucher.Entries.ShouldAllBe(e => e.BillAllocations.Count == 0);
    }

    [Fact]
    public void A_purchase_return_is_set_against_the_suppliers_invoice_when_its_goods_receipt_is_known()
    {
        var vendors = new VendorAccountsDay(
            [
                new VendorDebitNoteEntry("NHDN260000001", "Sri Balaji Pharma", "RA-17", 80m, 2m, 2m, 0m, SupplierInvoiceNumber: "SB/2291"),
                new VendorDebitNoteEntry("NHDN260000002", "Sri Balaji Pharma", null, 100m, 0m, 0m, 12m),
            ],
            []);

        var vouchers = TallyVoucherBuilder.Build(Book(vendors: vendors), Ledgers);

        Entry(vouchers.Single(v => v.Number == "NHDN260000001"), "Sri Balaji Pharma").ShouldSatisfyAllConditions(
            e => e.IsDebit.ShouldBeTrue(),
            e => e.BillAllocations.ShouldBe([new AccountingBillAllocation("SB/2291", AccountingBillType.AgainstRef, 84m)]));
        Entry(vouchers.Single(v => v.Number == "NHDN260000002"), "Sri Balaji Pharma").BillAllocations
            .ShouldBe([new AccountingBillAllocation(null, AccountingBillType.OnAccount, 112m)]);
    }

    [Fact]
    public void A_supplier_payable_adjustment_stands_on_the_suppliers_account_whichever_way_it_goes()
    {
        var vendors = new VendorAccountsDay([],
        [
            new VendorPayableAdjustmentEntry("NHPA260000001", "Sri Balaji Pharma", Reduce: true, 1_250m, "CN-SB-77"),
            new VendorPayableAdjustmentEntry("NHPA260000002", "Sri Balaji Pharma", Reduce: false, 300m, null),
        ]);

        var vouchers = TallyVoucherBuilder.Build(Book(vendors: vendors), Ledgers);

        Entry(vouchers.Single(v => v.Number == "NHPA260000001"), "Sri Balaji Pharma").ShouldSatisfyAllConditions(
            e => e.IsDebit.ShouldBeTrue(),
            e => e.BillAllocations.ShouldBe([new AccountingBillAllocation(null, AccountingBillType.OnAccount, 1_250m)]));
        Entry(vouchers.Single(v => v.Number == "NHPA260000002"), "Sri Balaji Pharma").ShouldSatisfyAllConditions(
            e => e.IsDebit.ShouldBeFalse(),
            e => e.BillAllocations.ShouldBe([new AccountingBillAllocation(null, AccountingBillType.OnAccount, 300m)]));
    }

    [Fact]
    public void A_line_merged_with_a_part_that_names_no_bill_has_that_part_on_account()
    {
        // One ledger for patients and insurers: the payer's share and the patient's share land on the same line.
        var oneLedger = Ledgers with { LedgerPerPayer = false, PayerReceivables = "Patient Receivables" };

        var voucher = TallyVoucherBuilder.Build(Book(sales: [InsuredSale]), oneLedger).Single();

        var line = Entry(voucher, "Patient Receivables");
        line.Amount.ShouldBe(1_490m);
        line.BillAllocations.ShouldBe(
        [
            new AccountingBillAllocation("NHOB260000001", AccountingBillType.NewRef, 1_000m),
            new AccountingBillAllocation(null, AccountingBillType.OnAccount, 490m),
        ]);
    }

    [Fact]
    public void Every_line_with_bills_adds_up_to_its_amount_and_every_voucher_balances()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(
            sales: [InsuredSale, InsuredSale with { BillNumber = "NHOB260000002", IsCancellation = true }],
            receipts: [new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.PayerSettlement, "Star Health", [new TallyPayment(PaymentMode.Insurance, 900m)]) { BillNumber = "NHOB260000001" }],
            disallowances: [new TallyDisallowance("NHCL26000001/0198a1", Day, "Star Health", 100m) { BillNumber = "NHOB260000001" }],
            vendors: new VendorAccountsDay(
                [new VendorDebitNoteEntry("NHDN260000001", "Sri Balaji Pharma", null, 80m, 2m, 2m, 0m, "SB/2291")],
                [new VendorPayableAdjustmentEntry("NHPA260000001", "Sri Balaji Pharma", true, 1_250m, null)])), Ledgers);

        vouchers.Count.ShouldBe(6);
        vouchers.ToList().ForEach(ShouldAddUp);
        vouchers.ShouldAllBe(v => v.Entries.Where(e => e.IsDebit).Sum(e => e.Amount) == v.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
    }

    [Fact]
    public void A_bill_reference_is_part_of_what_was_sent_but_a_voucher_without_one_keeps_its_fingerprint()
    {
        var sale = TallyVoucherBuilder.Build(Book(sales: [InsuredSale]), Ledgers).Single();
        var withoutBills = sale with { Entries = sale.Entries.Select(e => e with { BillAllocations = [] }).ToList() };
        var otherBill = sale with
        {
            Entries = sale.Entries.Select(e => e.BillAllocations.Count == 0 ? e : e with { BillAllocations = [new("NHOB260000009", AccountingBillType.NewRef, e.Amount)] }).ToList(),
        };

        TallyTracking.Fingerprint(sale).ShouldNotBe(TallyTracking.Fingerprint(withoutBills));
        TallyTracking.Fingerprint(sale).ShouldNotBe(TallyTracking.Fingerprint(otherBill));
        TallyTracking.Fingerprint(withoutBills).ShouldBe(TallyTracking.Fingerprint(withoutBills with { Entries = withoutBills.Entries.Reverse().ToList() }));
    }
}
