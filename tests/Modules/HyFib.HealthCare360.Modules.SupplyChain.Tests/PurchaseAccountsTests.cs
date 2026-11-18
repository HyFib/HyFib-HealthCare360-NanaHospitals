using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>
/// A posted goods receipt as the purchase Billing's Tally day book posts (N11): the supplier's invoice, the input GST by
/// rate and the round-off, adding up to what the receipt owes the supplier, with the supplier's GSTIN and state.
/// </summary>
public sealed class PurchaseAccountsTests
{
    private static readonly DateOnly Received = new(2026, 9, 25);

    private static GoodsReceiptNote Receipt(bool interState = false)
    {
        var receipt = new GoodsReceiptNote
        {
            Number = "NHGRN260000001",
            VendorInvoiceNumber = " SB/2291 ",
            VendorInvoiceDate = new DateOnly(2026, 9, 23),
            ReceivedOn = Received,
            IsInterState = interState,
            Lines =
            [
                new GoodsReceiptLine { ItemName = "Paracetamol 500 mg", Quantity = 37, Rate = 9.01m, GstRate = 5m },
                new GoodsReceiptLine { ItemName = "Amoxicillin 500 mg", Quantity = 10, Rate = 45.55m, DiscountPercent = 2.5m, GstRate = 12m },
                new GoodsReceiptLine { ItemName = "Cetirizine 10 mg", Quantity = 3, Rate = 17.33m, GstRate = 12m },
            ],
        };
        receipt.Recalculate();
        return receipt;
    }

    [Fact]
    public void A_goods_receipt_gives_its_suppliers_invoice_and_input_gst_by_rate_adding_up_to_what_it_owes()
    {
        var receipt = Receipt();

        var entry = PurchaseAccountsDayBookSource.Entry(receipt, "Sri Balaji Pharma", " 33AABCS1234K1Z5 ", "Kerala", pharmacyStore: true);

        entry.ShouldSatisfyAllConditions(
            e => e.Number.ShouldBe("NHGRN260000001"),
            e => e.ReceivedOn.ShouldBe(Received),
            e => e.SupplierInvoiceNumber.ShouldBe("SB/2291"),
            e => e.SupplierInvoiceDate.ShouldBe(new DateOnly(2026, 9, 23)),
            e => e.VendorGstin.ShouldBe("33AABCS1234K1Z5"),
            e => e.VendorStateCode.ShouldBe("33", "The GSTIN's state, not the address's."),
            e => e.PharmacyStore.ShouldBeTrue(),
            e => e.Tax.Select(t => t.GstRate).ShouldBe([5m, 12m]),
            e => e.RoundOff.ShouldBe(receipt.RoundOff),
            e => e.Total.ShouldBe(receipt.NetAmount));
        entry.Tax.Sum(t => t.Taxable).ShouldBe(receipt.TaxableAmount);
        entry.Tax.Sum(t => t.Cgst).ShouldBe(receipt.CgstAmount);
        entry.Tax.Sum(t => t.Sgst).ShouldBe(receipt.SgstAmount);
        entry.Tax.Single(t => t.GstRate == 12m).Taxable.ShouldBe(receipt.Lines.Where(l => l.GstRate == 12m).Sum(l => l.TaxableAmount));
    }

    [Fact]
    public void A_supplier_in_another_state_charges_igst()
    {
        var receipt = Receipt(interState: true);

        var entry = PurchaseAccountsDayBookSource.Entry(receipt, "Karnataka Surgicals", "29AAACK9876L1Z2", null, pharmacyStore: false);

        entry.Tax.ShouldAllBe(t => t.Cgst == 0m && t.Sgst == 0m);
        entry.Tax.Sum(t => t.Igst).ShouldBe(receipt.IgstAmount);
        entry.Total.ShouldBe(receipt.NetAmount);
        (entry.VendorStateCode, entry.PharmacyStore).ShouldBe(("29", false));
    }

    [Fact]
    public void An_unregistered_suppliers_state_comes_from_its_address()
    {
        var entry = PurchaseAccountsDayBookSource.Entry(Receipt(), "Theni Medicals", null, "Tamilnadu", pharmacyStore: true);

        (entry.VendorGstin, entry.VendorStateCode).ShouldBe((null, "33"));
        PurchaseAccountsDayBookSource.Entry(Receipt(), "Theni Medicals", " ", null, pharmacyStore: true).VendorStateCode.ShouldBeNull();
    }
}
