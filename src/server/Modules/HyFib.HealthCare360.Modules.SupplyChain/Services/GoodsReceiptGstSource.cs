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
            .Select(g => new { g.Id, g.VendorId, g.VendorInvoiceNumber, g.VendorInvoiceDate, g.ReceivedOn, g.TaxableAmount, g.IgstAmount, g.CgstAmount, g.SgstAmount })
            .ToListAsync(cancellationToken);

        // A vendor deleted since still supplied these goods: its GSTIN is read without the soft-delete filter, on its own.
        var vendorIds = receipts.Select(g => g.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().IgnoreQueryFilters()
            .Where(v => vendorIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => (v.Gstin, v.Name), cancellationToken);
        return receipts
            .Select(g =>
            {
                var vendor = vendors.GetValueOrDefault(g.VendorId);
                return new GstPurchase("grn", g.Id, vendor.Gstin, vendor.Name ?? string.Empty, g.VendorInvoiceNumber, g.VendorInvoiceDate ?? g.ReceivedOn,
                    g.TaxableAmount, g.IgstAmount, g.CgstAmount, g.SgstAmount);
            })
            .ToList();
    }
}
