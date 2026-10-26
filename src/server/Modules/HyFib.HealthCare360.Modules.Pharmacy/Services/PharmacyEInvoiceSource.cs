using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// The pharmacy's B2B invoices (counter sales to a buyer registered for GST) for Billing's e-invoice register, through the
/// Platform contract <see cref="IEInvoiceSource"/>. Medicines sell at MRP, GST included, so a line goes to the portal at
/// its taxable value: the unit price is that value per unit, with no separate discount.
/// </summary>
internal sealed class PharmacyEInvoiceSource(HospitalDbContext db) : IEInvoiceSource
{
    public const string Key = "pharmacy";

    public string Source => Key;

    public async Task<IReadOnlyList<EInvoiceCandidate>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var start = HospitalClock.StartOfDayUtc(from);
        var end = HospitalClock.StartOfDayUtc(to.AddDays(1));
        var sales = await db.Set<PharmacySale>().AsNoTracking()
            .Where(s => s.FacilityId == facilityId && s.BuyerGstin != null && s.SoldAt >= start && s.SoldAt < end)
            .Select(s => new { s.Id, s.InvoiceNumber, s.SoldAt, s.BuyerGstin, s.BuyerLegalName, s.NetAmount, s.Status })
            .ToListAsync(cancellationToken);
        return sales
            .Select(s => new EInvoiceCandidate(Key, s.Id, s.InvoiceNumber, HospitalClock.ToLocalDate(s.SoldAt), s.BuyerGstin!, s.BuyerLegalName ?? string.Empty,
                s.NetAmount, s.Status == SaleStatus.Cancelled))
            .ToList();
    }

    public async Task<EInvoiceDocument?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var sale = await db.Set<PharmacySale>().AsNoTracking().Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (sale?.BuyerGstin is not { } gstin)
        {
            return null;
        }

        var items = sale.Items
            .OrderBy(i => i.ItemName, StringComparer.Ordinal)
            .Select(i =>
            {
                var unitPrice = i.Quantity == 0 ? 0m : Math.Round(i.TaxableAmount / i.Quantity, 3);
                return new EInvoiceItem(
                    i.ItemName, IsService: false, i.HsnCode, i.Quantity, unitPrice, i.TaxableAmount, 0m, i.TaxableAmount, i.GstRate, i.IgstAmount,
                    i.CgstAmount, i.SgstAmount, i.TaxableAmount + i.IgstAmount + i.CgstAmount + i.SgstAmount);
            })
            .ToList();
        return new EInvoiceDocument(
            Key, sale.Id, sale.FacilityId, sale.InvoiceNumber, HospitalClock.ToLocalDate(sale.SoldAt),
            new EInvoiceBuyer(gstin, sale.BuyerLegalName ?? string.Empty, sale.BuyerAddress, null, null), sale.PlaceOfSupply ?? string.Empty, items,
            sale.RoundOff, sale.NetAmount, sale.Status == SaleStatus.Cancelled);
    }
}
