using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// The stores' goods receipts of a day as purchases for Billing's Tally day book (<see cref="IPurchaseAccountsSource"/>):
/// those posted into stock and received on the day, as GSTR-3B takes their input credit (<see cref="GoodsReceiptGstSource"/>),
/// with the supplier's GSTIN and state from the vendor master and whether the store is a pharmacy.
/// </summary>
internal sealed class PurchaseAccountsDayBookSource(HospitalDbContext db) : IPurchaseAccountsSource
{
    public async Task<IReadOnlyList<PurchaseEntry>> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken)
    {
        var receipts = await db.Set<GoodsReceiptNote>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(g => g.Lines)
            .Where(g => g.FacilityId == facilityId && g.Status == GoodsReceiptStatus.Posted && g.ReceivedOn == date)
            .ToListAsync(cancellationToken);
        if (receipts.Count == 0)
        {
            return [];
        }

        // A vendor or store deleted since still received these goods: they are read without the soft-delete filter.
        var vendorIds = receipts.Select(g => g.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().IgnoreQueryFilters().AsNoTracking()
            .Where(v => vendorIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Name, v.Gstin, v.Address.State })
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        var storeIds = receipts.Select(g => g.StoreId).Distinct().ToList();
        var pharmacies = (await db.Set<Store>().IgnoreQueryFilters().AsNoTracking()
                .Where(s => storeIds.Contains(s.Id) && s.Type == StoreType.Pharmacy)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return receipts
            .OrderBy(g => g.PostedAt).ThenBy(g => g.Number, StringComparer.Ordinal)
            .Select(g =>
            {
                var vendor = vendors.GetValueOrDefault(g.VendorId);
                return Entry(g, vendor?.Name ?? string.Empty, vendor?.Gstin, vendor?.State, pharmacies.Contains(g.StoreId));
            })
            .ToList();
    }

    /// <summary>
    /// A posted goods receipt as a purchase (pure, unit-tested): the supplier's invoice, its taxable value and input GST by
    /// rate and its round-off, which add up to what the receipt owes the supplier (<see cref="GoodsReceiptNote.NetAmount"/>).
    /// </summary>
    /// <param name="vendorState">The state on the vendor's address, used when the vendor has no GSTIN.</param>
    public static PurchaseEntry Entry(GoodsReceiptNote receipt, string vendorName, string? vendorGstin, string? vendorState, bool pharmacyStore) =>
        new(
            receipt.Number,
            receipt.ReceivedOn,
            vendorName,
            string.IsNullOrWhiteSpace(vendorGstin) ? null : vendorGstin.Trim(),
            Vendor.GstStateCode(string.IsNullOrWhiteSpace(vendorGstin) ? null : vendorGstin.Trim()) ?? IndianStates.Code(vendorState),
            receipt.VendorInvoiceNumber.Trim(),
            receipt.VendorInvoiceDate,
            pharmacyStore,
            SupplierTax.ByRate(receipt.Lines.Select(l => (l.GstRate, l.TaxableAmount, l.CgstAmount, l.SgstAmount, l.IgstAmount))),
            receipt.RoundOff);
}
