namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>
/// A supplier's document (a goods receipt, a debit note) as the accounts take it: its taxable value and input GST by rate,
/// adding up to the document's own totals, since Tally posts input GST to a ledger per rate (pure, unit-tested).
/// </summary>
public static class SupplierTax
{
    public static IReadOnlyList<SupplierTaxLine> ByRate(IEnumerable<(decimal Rate, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst)> lines) =>
        lines.GroupBy(l => l.Rate)
            .OrderBy(g => g.Key)
            .Select(g => new SupplierTaxLine(g.Key, g.Sum(l => l.Taxable), g.Sum(l => l.Cgst), g.Sum(l => l.Sgst), g.Sum(l => l.Igst)))
            .ToList();
}
