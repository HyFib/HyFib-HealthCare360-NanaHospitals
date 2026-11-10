using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// The stores' posted goods receipts as purchase invoices for GSTR-3B table 4 (input tax credit), by the day the goods
/// were received (CGST Act s.16(2)(b): credit follows receipt), with the supplier's GSTIN from the vendor master; and the
/// posted debit notes for goods sent back to suppliers, by the day they were posted, as returns that reduce the credit.
/// </summary>
internal sealed class GoodsReceiptGstSource(HospitalDbContext db) : IGstPurchaseSource
{
    public async Task<IReadOnlyList<GstPurchase>> InwardAsync(IReadOnlyCollection<Guid> facilityIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var receipts = await db.Set<GoodsReceiptNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(g => facilityIds.Contains(g.FacilityId) && g.Status == GoodsReceiptStatus.Posted && g.ReceivedOn >= from && g.ReceivedOn <= to)
            .Select(g => new { g.Id, g.VendorId, g.VendorInvoiceNumber, g.VendorInvoiceDate, g.ReceivedOn, g.TaxableAmount, g.IgstAmount, g.CgstAmount, g.SgstAmount })
            .ToListAsync(cancellationToken);

        var (start, end) = HospitalClock.Range(from, to);
        var returns = await db.Set<VendorDebitNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(n => facilityIds.Contains(n.FacilityId) && n.Status == VendorDocumentStatus.Posted && n.PostedAt >= start && n.PostedAt < end)
            .Select(n => new { n.Id, n.VendorId, n.Number, n.PostedAt, n.TaxableAmount, n.IgstAmount, n.CgstAmount, n.SgstAmount })
            .ToListAsync(cancellationToken);

        // A vendor deleted since still supplied these goods: its GSTIN is read without the soft-delete filter, on its own.
        var vendorIds = receipts.Select(g => g.VendorId).Concat(returns.Select(n => n.VendorId)).Distinct().ToList();
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
            .Concat(returns.Select(n =>
            {
                var vendor = vendors.GetValueOrDefault(n.VendorId);
                return new GstPurchase("vendor_debit_note", n.Id, vendor.Gstin, vendor.Name ?? string.Empty, n.Number, HospitalClock.ToLocalDate(n.PostedAt!.Value),
                    n.TaxableAmount, n.IgstAmount, n.CgstAmount, n.SgstAmount, IsReturn: true);
            }))
            .ToList();
    }
}
