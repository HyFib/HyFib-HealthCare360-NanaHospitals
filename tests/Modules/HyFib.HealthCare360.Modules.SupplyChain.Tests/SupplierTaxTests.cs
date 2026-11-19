using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>A supplier's document by GST rate, as Tally posts its input GST (a ledger per rate).</summary>
public sealed class SupplierTaxTests
{
    [Fact]
    public void Lines_are_summed_by_rate_lowest_first_and_add_up_to_the_document()
    {
        (decimal, decimal, decimal, decimal, decimal)[] lines =
        [
            (12m, 1_000m, 60m, 60m, 0m),
            (5m, 333.33m, 8.33m, 8.34m, 0m),
            (12m, 250.50m, 15.03m, 15.03m, 0m),
            (0m, 80m, 0m, 0m, 0m),
        ];

        var byRate = SupplierTax.ByRate(lines);

        byRate.ShouldBe(
        [
            new SupplierTaxLine(0m, 80m, 0m, 0m),
            new SupplierTaxLine(5m, 333.33m, 8.33m, 8.34m),
            new SupplierTaxLine(12m, 1_250.50m, 75.03m, 75.03m),
        ]);
        byRate.Sum(l => l.Total).ShouldBe(lines.Sum(l => l.Item2 + l.Item3 + l.Item4 + l.Item5));
    }

    [Fact]
    public void A_supplier_in_another_state_charges_igst_by_rate()
    {
        var debitNote = new VendorDebitNote { IsInterState = true };
        debitNote.AddLine(new StockBatch { PurchaseRate = 20m, GstRate = 5m, BatchNumber = "B1" }, "Paracetamol", 4m);
        debitNote.AddLine(new StockBatch { PurchaseRate = 50m, GstRate = 12m, BatchNumber = "B2" }, "Amoxicillin", 2m);

        var byRate = SupplierTax.ByRate(debitNote.Lines.Select(l => (l.GstRate, l.TaxableAmount, l.CgstAmount, l.SgstAmount, l.IgstAmount)));

        byRate.ShouldBe([new SupplierTaxLine(5m, 80m, 0m, 0m, 4m), new SupplierTaxLine(12m, 100m, 0m, 0m, 12m)]);
        byRate.Sum(l => l.Total).ShouldBe(debitNote.TotalAmount);
    }

    [Fact]
    public void A_document_without_lines_has_no_rates() => SupplierTax.ByRate([]).ShouldBeEmpty();
}
