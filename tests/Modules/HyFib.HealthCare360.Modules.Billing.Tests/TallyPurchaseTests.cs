using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tally;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The pharmacy's purchases, party ledger wise (N11): each goods receipt a Purchase voucher crediting the supplier's
/// ledger with a new bill named by the supplier's invoice, with purchases and input GST by rate, and the supplier's invoice
/// and GST details for GSTR-2B; a purchase return is then set against that bill.
/// </summary>
public sealed class TallyPurchaseTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly TallyLedgerMap Ledgers = new();

    /// <summary>Medicines at 12% and 5% from a supplier in the state, rounded up by 0.44 to the rupee.</summary>
    private static readonly PurchaseEntry Medicines = new(
        "NHGRN260000001", Day, "Sri Balaji Pharma", "33AABCS1234K1Z5", "33", "SB/2291", new DateOnly(2026, 9, 23), PharmacyStore: true,
        [new SupplierTaxLine(5m, 333.33m, 8.33m, 8.34m), new SupplierTaxLine(12m, 1_250.50m, 75.03m, 75.03m)], 0.44m);

    /// <summary>Gloves for the central store from a supplier in Karnataka (IGST), rounded down.</summary>
    private static readonly PurchaseEntry Gloves = new(
        "NHGRN260000002", Day, "Karnataka Surgicals", "29AAACK9876L1Z2", "29", "KS-881", null, PharmacyStore: false,
        [new SupplierTaxLine(12m, 1_000.40m, 0m, 0m, 120.05m)], -0.45m);

    private static TallyDayBook Book(params PurchaseEntry[] purchases) =>
        new TallyDayBook(Day, [], [], [], [], [], [], []) with { Purchases = purchases, FacilityStateCode = "33" };

    private static AccountingEntry Entry(AccountingVoucher voucher, string ledger) => voucher.Entries.Single(e => e.LedgerName == ledger);

    private static void ShouldBalance(AccountingVoucher voucher) =>
        voucher.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount), voucher.Number);

    [Fact]
    public void A_goods_receipt_is_a_purchase_credited_to_the_supplier_against_a_new_bill_of_its_invoice()
    {
        var voucher = TallyVoucherBuilder.Build(Book(Medicines), Ledgers).ShouldHaveSingleItem();

        voucher.ShouldSatisfyAllConditions(
            v => v.VoucherType.ShouldBe("Purchase"),
            v => v.Number.ShouldBe("NHGRN260000001"),
            v => v.Reference.ShouldBe("NHGRN260000001"),
            v => v.SupplierInvoiceNumber.ShouldBe("SB/2291"),
            v => v.SupplierInvoiceDate.ShouldBe(new DateOnly(2026, 9, 23)),
            v => v.Party.ShouldBe(new AccountingParty("Sri Balaji Pharma", "33AABCS1234K1Z5", "Tamil Nadu", "Tamil Nadu")),
            v => v.Narration.ShouldBe("Goods receipt NHGRN260000001: Sri Balaji Pharma invoice SB/2291 of 23-Sep-2026"));
        ShouldBalance(voucher);
        Entry(voucher, "Sri Balaji Pharma").ShouldSatisfyAllConditions(
            e => e.IsDebit.ShouldBeFalse(),
            e => e.Amount.ShouldBe(1_751m),
            e => e.BillAllocations.ShouldBe([new AccountingBillAllocation("SB/2291", AccountingBillType.NewRef, 1_751m)]));
        Entry(voucher, "Purchase Accounts @ 5%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(333.33m), e => e.IsDebit.ShouldBeTrue());
        Entry(voucher, "Purchase Accounts @ 12%").Amount.ShouldBe(1_250.50m);
        Entry(voucher, "Input CGST @ 2.5%").Amount.ShouldBe(8.33m);
        Entry(voucher, "Input SGST @ 2.5%").Amount.ShouldBe(8.34m);
        Entry(voucher, "Input CGST @ 6%").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(75.03m), e => e.IsDebit.ShouldBeTrue());
        Entry(voucher, "Round Off").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(0.44m), e => e.IsDebit.ShouldBeTrue());
    }

    [Fact]
    public void Only_the_pharmacy_stores_purchases_are_posted_unless_the_facility_chooses_every_store_or_none()
    {
        TallyVoucherBuilder.Build(Book(Medicines, Gloves), Ledgers).Select(v => v.Number).ShouldBe(["NHGRN260000001"]);
        TallyVoucherBuilder.Build(Book(Medicines, Gloves), Ledgers with { PurchasesFrom = TallyPurchaseStores.All }).Select(v => v.Number)
            .ShouldBe(["NHGRN260000001", "NHGRN260000002"]);
        TallyVoucherBuilder.Build(Book(Medicines, Gloves), Ledgers with { PurchasesFrom = TallyPurchaseStores.None }).ShouldBeEmpty();
    }

    [Fact]
    public void A_purchase_from_another_state_takes_igst_by_rate_and_a_rounding_down_is_credited()
    {
        var voucher = TallyVoucherBuilder.Build(Book(Gloves), Ledgers with { PurchasesFrom = TallyPurchaseStores.All }).Single();

        ShouldBalance(voucher);
        Entry(voucher, "Input IGST @ 12%").Amount.ShouldBe(120.05m);
        Entry(voucher, "Round Off").ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(0.45m), e => e.IsDebit.ShouldBeFalse());
        Entry(voucher, "Karnataka Surgicals").Amount.ShouldBe(1_120m);
        voucher.Party.ShouldBe(new AccountingParty("Karnataka Surgicals", "29AAACK9876L1Z2", "Karnataka", "Tamil Nadu"));
        voucher.SupplierInvoiceDate.ShouldBeNull();
        voucher.Narration.ShouldBe("Goods receipt NHGRN260000002: Karnataka Surgicals invoice KS-881");
    }

    [Fact]
    public void A_purchase_return_is_set_against_the_bill_its_purchase_made()
    {
        var vouchers = TallyVoucherBuilder.Build(
            Book(Medicines) with
            {
                Vendors = new VendorAccountsDay([new VendorDebitNoteEntry("NHDN260000001", "Sri Balaji Pharma", null, 100m, 2.5m, 2.5m, 0m, "SB/2291")], []),
            },
            Ledgers);

        var made = Entry(vouchers.Single(v => v.Number == "NHGRN260000001"), "Sri Balaji Pharma").BillAllocations.ShouldHaveSingleItem();
        var against = Entry(vouchers.Single(v => v.Number == "NHDN260000001"), "Sri Balaji Pharma").BillAllocations.ShouldHaveSingleItem();
        (made.Name, made.Type).ShouldBe(("SB/2291", AccountingBillType.NewRef));
        (against.Name, against.Type, against.Amount).ShouldBe(("SB/2291", AccountingBillType.AgainstRef, 105m));
    }

    [Fact]
    public void Without_a_ledger_per_supplier_or_per_rate_the_purchase_uses_the_single_ledgers()
    {
        var voucher = TallyVoucherBuilder.Build(Book(Medicines), Ledgers with { LedgerPerVendor = false, SalesLedgerPerGstRate = false, GstLedgerPerRate = false }).Single();

        ShouldBalance(voucher);
        Entry(voucher, "Sundry Creditors").BillAllocations.ShouldHaveSingleItem().Name.ShouldBe("SB/2291");
        Entry(voucher, "Purchase Accounts").Amount.ShouldBe(1_583.83m);
        Entry(voucher, "Input CGST").Amount.ShouldBe(83.36m);
        voucher.Party!.LedgerName.ShouldBe("Sundry Creditors");
    }

    [Fact]
    public void An_unregistered_supplier_has_no_gstin_and_its_state_from_its_address()
    {
        var local = Medicines with { VendorGstin = null, VendorStateCode = "33" };

        TallyVoucherBuilder.Build(Book(local), Ledgers).Single().Party.ShouldBe(new AccountingParty("Sri Balaji Pharma", null, "Tamil Nadu", "Tamil Nadu"));
    }

    [Fact]
    public void A_purchase_is_named_after_its_goods_receipt_when_the_suppliers_invoice_number_is_missing()
    {
        var voucher = TallyVoucherBuilder.Build(Book(Medicines with { SupplierInvoiceNumber = " " }), Ledgers).Single();

        voucher.SupplierInvoiceNumber.ShouldBe("NHGRN260000001");
        Entry(voucher, "Sri Balaji Pharma").BillAllocations.ShouldHaveSingleItem().Name.ShouldBe("NHGRN260000001");
    }

    [Fact]
    public void The_purchase_settings_have_their_defaults_and_are_checked()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var validator = new TallyLedgerMapValidator();

        (Ledgers.VoucherTypes.Purchase, Ledgers.Purchases, Ledgers.PurchasesFrom).ShouldBe(("Purchase", "Purchase Accounts", TallyPurchaseStores.Pharmacy));
        JsonSerializer.Deserialize<TallyLedgerMap>("""{"purchasesFrom":"All","purchases":"Purchases - Pharmacy"}""", web)!
            .ShouldSatisfyAllConditions(m => m.PurchasesFrom.ShouldBe(TallyPurchaseStores.All), m => m.Purchases.ShouldBe("Purchases - Pharmacy"));
        JsonSerializer.Serialize(Ledgers, web).ShouldContain("\"purchasesFrom\":\"Pharmacy\"");
        validator.Validate(Ledgers with { Purchases = " " }).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(TallyLedgerMap.Purchases));
        validator.Validate(Ledgers with { PurchasesFrom = (TallyPurchaseStores)7 }).IsValid.ShouldBeFalse();
        validator.Validate(Ledgers with { VoucherTypes = new TallyVoucherTypes { Purchase = "" } }).Errors.ShouldHaveSingleItem()
            .PropertyName.ShouldBe("VoucherTypes.Purchase");
        TallyLedgerMaps.Normalize(Ledgers with { Purchases = " Purchase Accounts ", VoucherTypes = new TallyVoucherTypes { Purchase = " Purchase " } })
            .ShouldSatisfyAllConditions(m => m.Purchases.ShouldBe("Purchase Accounts"), m => m.VoucherTypes.Purchase.ShouldBe("Purchase"));
    }

    [Fact]
    public void A_day_with_only_purchases_is_not_empty() => Book(Medicines).IsEmpty.ShouldBeFalse();
}
