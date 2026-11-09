using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// The pharmacy's outward documents for Billing's GST returns: counter sales (B2B to a buyer registered for GST, else to
/// patients) and their returns, which are credit notes (CGST Act s.34(1)(b)). A medicine with GST is taxable; one sold at
/// 0% is nil-rated. A return's taxable value and GST are carved out of its refund at the sale line's rate, as the
/// pharmacy's day book and its e-invoice carve them; a return after the sale's GST could be reduced lowers no GST.
/// </summary>
internal sealed class PharmacyGstReturnSource(HospitalDbContext db) : IGstReturnSource
{
    public async Task<IReadOnlyList<GstReturnDocument>> OutwardAsync(IReadOnlyCollection<Guid> facilityIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var start = HospitalClock.StartOfDayUtc(from);
        var end = HospitalClock.StartOfDayUtc(to.AddDays(1));
        var documents = new List<GstReturnDocument>();
        var sales = await db.Set<PharmacySale>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(s => s.Items)
            .Where(s => facilityIds.Contains(s.FacilityId) && s.SoldAt >= start && s.SoldAt < end)
            .ToListAsync(cancellationToken);
        foreach (var sale in sales)
        {
            documents.Add(new GstReturnDocument(
                PharmacyEInvoiceSource.Key, sale.Id, GstDocumentKind.Invoice, sale.InvoiceNumber, HospitalClock.ToLocalDate(sale.SoldAt), sale.BuyerGstin,
                sale.BuyerLegalName ?? sale.CustomerName ?? "Patient", sale.PlaceOfSupply ?? string.Empty, sale.NetAmount, sale.Status == SaleStatus.Cancelled,
                sale.Items.OrderBy(i => i.ItemName, StringComparer.Ordinal)
                    .Select(i => Line(i.HsnCode, i.ItemName, i.Quantity, i.GstRate, i.TaxableAmount, i.IgstAmount, i.CgstAmount, i.SgstAmount))
                    .ToList(),
                "Pharmacy invoices"));
        }

        var returns = await db.Set<PharmacySaleReturn>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(r => r.Items)
            .Where(r => facilityIds.Contains(r.FacilityId) && r.ReturnedAt >= start && r.ReturnedAt < end)
            .ToListAsync(cancellationToken);
        var saleIds = returns.Select(r => r.SaleId).Distinct().ToList();
        var sold = await db.Set<PharmacySale>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(s => s.Items)
            .Where(s => saleIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);
        foreach (var saleReturn in returns)
        {
            var sale = sold.GetValueOrDefault(saleReturn.SaleId);
            var returnedOn = HospitalClock.ToLocalDate(saleReturn.ReturnedAt);
            var soldOn = sale is null ? returnedOn : HospitalClock.ToLocalDate(sale.SoldAt);
            var lines = sale is null || returnedOn > GstCreditNotes.LastDay(soldOn)
                ? []
                : saleReturn.Items.OrderBy(i => i.ItemName, StringComparer.Ordinal).Select(i =>
                {
                    var line = sale.Items.FirstOrDefault(s => s.Id == i.SaleItemId);
                    var rate = line?.GstRate ?? 0m;
                    var split = GstCalculator.FromInclusive(i.RefundAmount, rate, sale.IsInterState);
                    return Line(line?.HsnCode, i.ItemName, i.Quantity, rate, split.Taxable, split.Igst, split.Cgst, split.Sgst);
                }).ToList();
            documents.Add(new GstReturnDocument(
                PharmacyReturnEInvoiceSource.Key, saleReturn.Id, GstDocumentKind.CreditNote, saleReturn.ReturnNumber, returnedOn, sale?.BuyerGstin,
                sale?.BuyerLegalName ?? sale?.CustomerName ?? "Patient", sale?.PlaceOfSupply ?? string.Empty, saleReturn.RefundAmount, IsCancelled: false, lines,
                "Pharmacy returns", sale?.InvoiceNumber, sale is null ? null : soldOn, sale?.NetAmount, sale?.PlaceOfSupply));
        }

        return documents;
    }

    private static GstReturnLine Line(string? hsn, string name, decimal quantity, decimal rate, decimal taxable, decimal igst, decimal cgst, decimal sgst) =>
        new(hsn, name, IsService: false, "NOS", quantity, rate, rate > 0m ? GstSupplyKind.Taxable : GstSupplyKind.NilRated, taxable, igst, cgst, sgst);
}
