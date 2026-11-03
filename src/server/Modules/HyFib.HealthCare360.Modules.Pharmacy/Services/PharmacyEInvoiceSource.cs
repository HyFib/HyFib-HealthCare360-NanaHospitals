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

/// <summary>
/// The pharmacy's returns of B2B sales, for Billing's e-invoice register: a return is a credit note (CGST Act s.34(1)(b),
/// goods returned) and, from an e-invoicing hospital to a registered buyer, an e-invoice too (GST rule 48(4)). It goes to
/// the portal as a <c>CRN</c> naming the sale's invoice, each medicine at the taxable value and GST carved out of its
/// refund at the sale line's rate, as the pharmacy's day book carves them for the accounts. A return made after the time
/// to reduce the sale's GST (<see cref="GstCreditNotes.LastDay"/>) is not reported: it lowers no GST.
/// </summary>
internal sealed class PharmacyReturnEInvoiceSource(HospitalDbContext db) : IEInvoiceSource
{
    public const string Key = "pharmacyreturn";

    public string Source => Key;

    public async Task<IReadOnlyList<EInvoiceCandidate>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var start = HospitalClock.StartOfDayUtc(from);
        var end = HospitalClock.StartOfDayUtc(to.AddDays(1));
        var returns = await (
                from r in db.Set<PharmacySaleReturn>().AsNoTracking()
                join s in db.Set<PharmacySale>() on r.SaleId equals s.Id
                where r.FacilityId == facilityId && s.BuyerGstin != null && r.ReturnedAt >= start && r.ReturnedAt < end
                select new { r.Id, r.ReturnNumber, r.ReturnedAt, s.SoldAt, s.BuyerGstin, s.BuyerLegalName, r.RefundAmount, s.InvoiceNumber })
            .ToListAsync(cancellationToken);
        return returns
            .Where(r => HospitalClock.ToLocalDate(r.ReturnedAt) <= GstCreditNotes.LastDay(HospitalClock.ToLocalDate(r.SoldAt)))
            .Select(r => new EInvoiceCandidate(Key, r.Id, r.ReturnNumber, HospitalClock.ToLocalDate(r.ReturnedAt), r.BuyerGstin!, r.BuyerLegalName ?? string.Empty,
                r.RefundAmount, IsCancelled: false, EInvoiceDocumentTypes.CreditNote, r.InvoiceNumber))
            .ToList();
    }

    public async Task<EInvoiceDocument?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var saleReturn = await db.Set<PharmacySaleReturn>().AsNoTracking().Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (saleReturn is null)
        {
            return null;
        }

        var sale = await db.Set<PharmacySale>().AsNoTracking().Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == saleReturn.SaleId, cancellationToken);
        var returnedOn = HospitalClock.ToLocalDate(saleReturn.ReturnedAt);
        var soldOn = sale is null ? returnedOn : HospitalClock.ToLocalDate(sale.SoldAt);
        if (sale?.BuyerGstin is not { } gstin || returnedOn > GstCreditNotes.LastDay(soldOn))
        {
            return null;
        }

        var items = saleReturn.Items
            .OrderBy(i => i.ItemName, StringComparer.Ordinal)
            .Select(i =>
            {
                var sold = sale.Items.FirstOrDefault(s => s.Id == i.SaleItemId);
                var rate = sold?.GstRate ?? 0m;
                var split = GstCalculator.FromInclusive(i.RefundAmount, rate, sale.IsInterState);
                var unitPrice = i.Quantity == 0 ? 0m : Math.Round(split.Taxable / i.Quantity, 3);
                return new EInvoiceItem(
                    i.ItemName, IsService: false, sold?.HsnCode, i.Quantity, unitPrice, split.Taxable, 0m, split.Taxable, rate, split.Igst, split.Cgst,
                    split.Sgst, split.Total);
            })
            .ToList();
        return new EInvoiceDocument(
            Key, saleReturn.Id, saleReturn.FacilityId, saleReturn.ReturnNumber, returnedOn,
            new EInvoiceBuyer(gstin, sale.BuyerLegalName ?? string.Empty, sale.BuyerAddress, null, null), sale.PlaceOfSupply ?? string.Empty, items,
            0m, saleReturn.RefundAmount, IsCancelled: false, Type: EInvoiceDocumentTypes.CreditNote,
            Preceding: new EInvoicePreceding(PharmacyEInvoiceSource.Key, sale.Id, sale.InvoiceNumber, soldOn));
    }
}
