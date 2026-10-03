using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// The pharmacy counter's day for Billing's Tally day book (INT-TALLY), through the Platform contract
/// <see cref="ICounterSalesSource"/>. In-patient issues are not here: they reach the IP bill as charges.
/// </summary>
internal sealed class PharmacyDayBookSource(HospitalDbContext db) : ICounterSalesSource
{
    public const string Key = "pharmacy";

    public string Counter => Key;

    public async Task<CounterSalesDay> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken)
    {
        var (start, end) = HospitalClock.Range(date, date);
        var sales = await db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .Where(s => s.FacilityId == facilityId && s.SoldAt >= start && s.SoldAt < end && s.Status != SaleStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var returns = await db.Set<PharmacySaleReturn>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(r => r.Items)
            .Where(r => r.FacilityId == facilityId && r.ReturnedAt >= start && r.ReturnedAt < end)
            .ToListAsync(cancellationToken);
        var returnedItems = returns.SelectMany(r => r.Items).Select(i => i.SaleItemId).Distinct().ToList();
        var rates = await db.Set<PharmacySaleItem>().AsNoTracking()
            .Where(i => returnedItems.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.GstRate, cancellationToken);

        // Balances on account paid at the counter on the day: payments after the sale, of invoices still standing.
        var collected = await (
                from payment in db.Set<PharmacyPayment>()
                join sale in db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on payment.PharmacySaleId equals sale.Id
                where sale.FacilityId == facilityId && sale.Status != SaleStatus.Cancelled && !payment.IsRefund && payment.Mode != PaymentMode.Credit
                      && payment.ReceivedAt >= start && payment.ReceivedAt < end && payment.ReceivedAt != sale.SoldAt
                select new PharmacyCollection(payment.Mode, payment.Amount))
            .ToListAsync(cancellationToken);

        return PharmacyDayBook.Day(date, sales, returns, rates, collected);
    }
}

/// <summary>A balance on account paid at the counter.</summary>
internal sealed record PharmacyCollection(PaymentMode Mode, decimal Amount);

/// <summary>
/// Summarises the pharmacy's day for the accounts (pure, unit-tested):
/// <list type="bullet">
/// <item>Invoices sold that day, at their GST rates. Money taken with the invoice counts as paid at the counter; the
/// rest of the invoice went on the patient's account. A same-day cancellation removes the invoice, with its payments
/// and its refund, so it never reaches the books.</item>
/// <item>Returns booked that day: the GST is carved out of each line's refund at the line's rate. Money paid back counts
/// by mode; what was set against a balance due comes off the account.</item>
/// <item>Balances on account paid later, by mode.</item>
/// </list>
/// </summary>
internal static class PharmacyDayBook
{
    public static CounterSalesDay Day(
        DateOnly date,
        IReadOnlyList<PharmacySale> sales,
        IReadOnlyList<PharmacySaleReturn> returns,
        IReadOnlyDictionary<Guid, decimal> returnedItemRates,
        IReadOnlyList<PharmacyCollection> collected)
    {
        var standing = sales.Where(s => s.Status != SaleStatus.Cancelled).ToList();
        var sold = new CounterDocuments(
            standing.Count,
            Tax(standing.SelectMany(s => s.Items).Select(i => (i.GstRate, i.TaxableAmount, i.CgstAmount, i.SgstAmount))),
            standing.Sum(s => s.RoundOff),
            ByMode(standing.SelectMany(s => s.Payments.Where(p => !p.IsRefund && p.Mode != PaymentMode.Credit && p.ReceivedAt == s.SoldAt))
                .Select(p => (p.Mode, p.Amount))));

        var returned = new CounterDocuments(
            returns.Count,
            Tax(returns.SelectMany(r => r.Items).Select(i =>
            {
                var rate = returnedItemRates.GetValueOrDefault(i.SaleItemId);
                var split = GstCalculator.FromInclusive(i.RefundAmount, rate);
                return (rate, split.Taxable, split.Cgst, split.Sgst);
            })),
            0m,
            ByMode(returns.Where(r => r.CashRefund > 0).Select(r => (r.RefundMode == PaymentMode.Credit ? PaymentMode.Cash : r.RefundMode, r.CashRefund))));

        return new CounterSalesDay(PharmacyDayBookSource.Key, date, sold, returned, ByMode(collected.Select(c => (c.Mode, c.Amount))));
    }

    private static List<CounterTaxLine> Tax(IEnumerable<(decimal Rate, decimal Taxable, decimal Cgst, decimal Sgst)> lines) =>
        lines.GroupBy(l => l.Rate)
            .OrderBy(g => g.Key)
            .Select(g => new CounterTaxLine(g.Key, g.Sum(l => l.Taxable), g.Sum(l => l.Cgst), g.Sum(l => l.Sgst)))
            .ToList();

    private static List<CounterMoney> ByMode(IEnumerable<(PaymentMode Mode, decimal Amount)> payments) =>
        payments.Where(p => p.Mode != PaymentMode.Credit && p.Amount != 0)
            .GroupBy(p => p.Mode)
            .OrderBy(g => g.Key)
            .Select(g => new CounterMoney(g.Key switch
            {
                PaymentMode.Card => CounterPaymentMode.Card,
                PaymentMode.Upi => CounterPaymentMode.Upi,
                _ => CounterPaymentMode.Cash,
            }, g.Sum(p => p.Amount)))
            .ToList();
}
