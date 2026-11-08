using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// The stores' posted goods receipts as purchase invoices for GSTR-3B table 4 (input tax credit), by the day the goods
/// were received (CGST Act s.16(2)(b): credit follows receipt), with the supplier's GSTIN from the vendor master.
/// </summary>
internal sealed class GoodsReceiptGstSource(HospitalDbContext db) : IGstPurchaseSource
{
    public async Task<IReadOnlyList<GstPurchase>> InwardAsync(IReadOnlyCollection<Guid> facilityIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var receipts = await db.Set<GoodsReceiptNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(g => facilityIds.Contains(g.FacilityId) && g.Status == GoodsReceiptStatus.Posted && g.ReceivedOn >= from && g.ReceivedOn <= to)
            .Join(db.Set<Vendor>().IgnoreQueryFilters(), g => g.VendorId, v => v.Id, (g, v) => new
            {
                g.Id, v.Gstin, v.Name, g.VendorInvoiceNumber, g.VendorInvoiceDate, g.ReceivedOn, g.TaxableAmount, g.IgstAmount, g.CgstAmount, g.SgstAmount,
            })
            .ToListAsync(cancellationToken);
        return receipts
            .Select(g => new GstPurchase("grn", g.Id, g.Gstin, g.Name, g.VendorInvoiceNumber, g.VendorInvoiceDate ?? g.ReceivedOn, g.TaxableAmount, g.IgstAmount,
                g.CgstAmount, g.SgstAmount))
            .ToList();
    }
}
