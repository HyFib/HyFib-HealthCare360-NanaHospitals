using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// The pharmacy counter's day for Billing's Tally day book (INT-TALLY), summed (<see cref="ICounterSalesSource"/>) or invoice
/// by invoice (<see cref="ICounterInvoiceSource"/>), and its card and UPI payments for the card and UPI settlement match.
/// In-patient issues are not here: they reach the IP bill as charges.
/// </summary>
internal sealed class PharmacyDayBookSource(HospitalDbContext db) : ICounterSalesSource, ICounterInvoiceSource
{
    public const string Key = "pharmacy";

    public string Counter => Key;

    /// <summary>
    /// The day invoice by invoice: the invoices sold (a same-day cancellation leaves its invoice out), the returns booked
    /// and the balances paid on the day, with the sales they belong to read once, items and payments included.
    /// </summary>
    public async Task<CounterInvoicesDay> GetInvoicesAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken)
    {
        var (start, end) = HospitalClock.Range(date, date);
        var sales = db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments);
        var sold = await sales
            .Where(s => s.FacilityId == facilityId && s.SoldAt >= start && s.SoldAt < end && s.Status != SaleStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var returns = await db.Set<PharmacySaleReturn>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(r => r.Items)
            .Where(r => r.FacilityId == facilityId && r.ReturnedAt >= start && r.ReturnedAt < end)
            .ToListAsync(cancellationToken);

        // Invoices of other days whose goods came back, or whose balance was paid, on the day.
        var paidOn = await (
                from payment in db.Set<PharmacyPayment>()
                join sale in db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on payment.PharmacySaleId equals sale.Id
                where sale.FacilityId == facilityId && sale.Status != SaleStatus.Cancelled && !payment.IsRefund && payment.Mode != PaymentMode.Credit
                      && payment.ReceivedAt >= start && payment.ReceivedAt < end && payment.ReceivedAt != sale.SoldAt
                select sale.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
        var soldIds = sold.Select(s => s.Id).ToHashSet();
        var otherIds = returns.Select(r => r.SaleId).Concat(paidOn).Where(id => !soldIds.Contains(id)).Distinct().ToList();
        var others = otherIds.Count == 0 ? [] : await sales.Where(s => otherIds.Contains(s.Id)).ToListAsync(cancellationToken);

        return PharmacyDayBook.Invoices(date, sold, returns, sold.Concat(others).ToDictionary(s => s.Id));
    }

    /// <summary>
    /// Card and UPI payments on invoices, later collections included, and money paid back to a card or by UPI on a return
    /// or a cancellation (a refund carries the payment's own reference, under which the acquirer reports it). A cancelled
    /// invoice's card payment is still here: the terminal settles it, and its refund settles too.
    /// </summary>
    public async Task<IReadOnlyList<CounterCardPayment>> GetCardPaymentsAsync(Guid facilityId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await (
                from p in db.Set<PharmacyPayment>().AsNoTracking()
                join s in db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.PharmacySaleId equals s.Id
                where s.FacilityId == facilityId && (p.Mode == PaymentMode.Card || p.Mode == PaymentMode.Upi) && p.ReceivedAt >= @from && p.ReceivedAt < to
                orderby p.ReceivedAt
                select new CounterCardPayment(
                    p.Id, s.InvoiceNumber, p.Mode == PaymentMode.Card ? CounterPaymentMode.Card : CounterPaymentMode.Upi, p.Amount, p.Reference, p.ReceivedAt,
                    p.IsRefund, s.Status == SaleStatus.Cancelled, p.ApprovalCode))
            .ToListAsync(cancellationToken);

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
        var returnedSales = await (
                from i in db.Set<PharmacySaleItem>().AsNoTracking()
                join s in db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on i.PharmacySaleId equals s.Id
                where returnedItems.Contains(i.Id)
                select new { i.Id, s.IsInterState, s.SoldAt })
            .ToListAsync(cancellationToken);
        var interState = returnedSales.Where(r => r.IsInterState).Select(r => r.Id).ToHashSet();

        // A return after the time to reduce the sale's GST has passed (CGST Act s.34(2)) takes back the value, not the GST.
        var timeBarred = returnedSales.Where(r => date > GstCreditNotes.LastDay(HospitalClock.ToLocalDate(r.SoldAt))).Select(r => r.Id).ToHashSet();

        // Balances on account paid at the counter on the day: payments after the sale, of invoices still standing.
        var collected = await (
                from payment in db.Set<PharmacyPayment>()
                join sale in db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on payment.PharmacySaleId equals sale.Id
                where sale.FacilityId == facilityId && sale.Status != SaleStatus.Cancelled && !payment.IsRefund && payment.Mode != PaymentMode.Credit
                      && payment.ReceivedAt >= start && payment.ReceivedAt < end && payment.ReceivedAt != sale.SoldAt
                select new PharmacyCollection(payment.Mode, payment.Amount))
            .ToListAsync(cancellationToken);

        // Money paid back on the day's returns, in the mode each part went back in (a sale paid by card and in cash is paid
        // back in both): the refunds of invoices still standing, as a cancelled invoice leaves the books with its refunds.
        var paidBack = await (
                from payment in db.Set<PharmacyPayment>()
                join sale in db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on payment.PharmacySaleId equals sale.Id
                where sale.FacilityId == facilityId && sale.Status != SaleStatus.Cancelled && payment.IsRefund && payment.ReceivedAt >= start && payment.ReceivedAt < end
                select new PharmacyCollection(payment.Mode, payment.Amount))
            .ToListAsync(cancellationToken);

        return PharmacyDayBook.Day(date, sales, returns, rates, collected, interState, timeBarred, paidBack);
    }
}

/// <summary>Money that changed hands at the counter, by mode: a balance on account paid, or a refund paid back.</summary>
internal sealed record PharmacyCollection(PaymentMode Mode, decimal Amount);

/// <summary>
/// Summarises the pharmacy's day for the accounts (pure, unit-tested):
/// <list type="bullet">
/// <item>Invoices sold that day, at their GST rates. Money taken with the invoice counts as paid at the counter; the
/// rest of the invoice went on the patient's account. A same-day cancellation removes the invoice, with its payments
/// and its refund, so it never reaches the books.</item>
/// <item>Returns booked that day: the GST is carved out of each line's refund at the line's rate, as the return's credit
/// note to a registered buyer tells the Invoice Registration Portal; a return after the time to reduce the sale's GST
/// (CGST Act s.34(2)) takes back the value alone. Money paid back counts by the mode it went back in (the refunds of the
/// day: a return of a sale paid by card and in cash goes back in both); what was set against
/// a balance due comes off the account.</item>
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
        IReadOnlyList<PharmacyCollection> collected,
        IReadOnlySet<Guid>? interStateItems = null,
        IReadOnlySet<Guid>? timeBarredItems = null,
        IReadOnlyList<PharmacyCollection>? paidBack = null)
    {
        var standing = sales.Where(s => s.Status != SaleStatus.Cancelled).ToList();
        var sold = new CounterDocuments(
            standing.Count,
            Tax(standing.SelectMany(s => s.Items).Select(i => (i.GstRate, i.TaxableAmount, i.CgstAmount, i.SgstAmount, i.IgstAmount))),
            standing.Sum(s => s.RoundOff),
            ByMode(standing.SelectMany(s => s.Payments.Where(p => !p.IsRefund && p.Mode != PaymentMode.Credit && p.ReceivedAt == s.SoldAt))
                .Select(p => (p.Mode, p.Amount))));

        var returned = new CounterDocuments(
            returns.Count,
            Tax(returns.SelectMany(r => r.Items).Select(i =>
            {
                var rate = returnedItemRates.GetValueOrDefault(i.SaleItemId);
                var split = timeBarredItems?.Contains(i.SaleItemId) == true
                    ? new TaxBreakup(Money.Round(i.RefundAmount), 0m, 0m, 0m, 0m)
                    : GstCalculator.FromInclusive(i.RefundAmount, rate, interStateItems?.Contains(i.SaleItemId) == true);
                return (rate, split.Taxable, split.Cgst, split.Sgst, split.Igst);
            })),
            0m,
            paidBack is not null
                ? ByMode(paidBack.Select(p => (p.Mode, p.Amount)))
                : ByMode(returns.Where(r => r.CashRefund > 0).Select(r => (r.RefundMode == PaymentMode.Credit ? PaymentMode.Cash : r.RefundMode, r.CashRefund))));

        return new CounterSalesDay(PharmacyDayBookSource.Key, date, sold, returned, ByMode(collected.Select(c => (c.Mode, c.Amount))));
    }

    /// <summary>
    /// The day invoice by invoice (pure, unit-tested), by the same rules as <see cref="Day"/>: each invoice sold with the
    /// money taken with it; each return with the GST carved out of its refund at each line's rate (the value alone once
    /// the time to reduce the sale's GST has passed) and the money paid back for it, in the modes it went back in (the
    /// sale's refunds made with the return); and each invoice's balances paid on the day.
    /// </summary>
    /// <param name="sold">The invoices sold on the day; a cancelled one is left out.</param>
    /// <param name="returns">The returns booked on the day.</param>
    /// <param name="sales">Every sale the day's invoices, returns and balances paid belong to, with items and payments.</param>
    public static CounterInvoicesDay Invoices(
        DateOnly date, IReadOnlyList<PharmacySale> sold, IReadOnlyList<PharmacySaleReturn> returns, IReadOnlyDictionary<Guid, PharmacySale> sales)
    {
        var (start, end) = HospitalClock.Range(date, date);
        var invoices = sold.Where(s => s.Status != SaleStatus.Cancelled)
            .OrderBy(s => s.SoldAt).ThenBy(s => s.InvoiceNumber, StringComparer.Ordinal)
            .Select(s => new CounterInvoice(
                s.InvoiceNumber, s.SoldAt, s.PatientId, Clean(s.CustomerName), Buyer(s),
                Tax(s.Items.Select(i => (i.GstRate, i.TaxableAmount, i.CgstAmount, i.SgstAmount, i.IgstAmount))),
                s.RoundOff,
                Payments(s.Payments.Where(p => !p.IsRefund && p.Mode != PaymentMode.Credit && p.ReceivedAt == s.SoldAt))))
            .ToList();

        var credited = returns.OrderBy(r => r.ReturnedAt).ThenBy(r => r.ReturnNumber, StringComparer.Ordinal).Select(r =>
        {
            var sale = sales.GetValueOrDefault(r.SaleId);
            var rates = sale?.Items.ToDictionary(i => i.Id, i => i.GstRate) ?? [];
            var timeBarred = sale is not null && date > GstCreditNotes.LastDay(HospitalClock.ToLocalDate(sale.SoldAt));
            var tax = Tax(r.Items.Select(i =>
            {
                var rate = rates.GetValueOrDefault(i.SaleItemId);
                var split = timeBarred
                    ? new TaxBreakup(Money.Round(i.RefundAmount), 0m, 0m, 0m, 0m)
                    : GstCalculator.FromInclusive(i.RefundAmount, rate, sale?.IsInterState == true);
                return (rate, split.Taxable, split.Cgst, split.Sgst, split.Igst);
            }));

            // What went back with the return: the sale's refunds made with it, in each mode they went back in.
            var refunds = sale?.Payments.Where(p => p.IsRefund && p.ReceivedAt == r.ReturnedAt).ToList() ?? [];
            var paidBack = refunds.Count > 0 ? Payments(refunds)
                : r.CashRefund > 0 ? [new CounterPayment(ModeOf(r.RefundMode), r.CashRefund, r.RefundReference)]
                : [];
            return new CounterReturn(
                r.ReturnNumber, r.ReturnedAt, sale?.InvoiceNumber ?? string.Empty, r.PatientId ?? sale?.PatientId, Clean(sale?.CustomerName),
                sale is null ? null : Buyer(sale), tax, paidBack);
        }).ToList();

        var collections = sales.Values.Where(s => s.Status != SaleStatus.Cancelled)
            .Select(s => (Sale: s, Paid: s.Payments
                .Where(p => !p.IsRefund && p.Mode != PaymentMode.Credit && p.ReceivedAt >= start && p.ReceivedAt < end && p.ReceivedAt != s.SoldAt)
                .ToList()))
            .Where(c => c.Paid.Count > 0)
            .OrderBy(c => c.Sale.InvoiceNumber, StringComparer.Ordinal)
            .Select(c => new CounterCollection(c.Sale.InvoiceNumber, c.Sale.PatientId, Clean(c.Sale.CustomerName), Payments(c.Paid)))
            .ToList();

        return new CounterInvoicesDay(PharmacyDayBookSource.Key, date, invoices, credited, collections);
    }

    /// <summary>The registered buyer of a B2B sale, with the place of supply.</summary>
    private static CounterBuyer? Buyer(PharmacySale sale) =>
        string.IsNullOrWhiteSpace(sale.BuyerGstin) ? null : new CounterBuyer(sale.BuyerGstin.Trim(), Clean(sale.BuyerLegalName), Clean(sale.PlaceOfSupply));

    /// <summary>Each payment with its reference, in the order taken; credit is not money.</summary>
    private static List<CounterPayment> Payments(IEnumerable<PharmacyPayment> payments) =>
        payments.Where(p => p.Mode != PaymentMode.Credit && p.Amount != 0)
            .OrderBy(p => p.ReceivedAt)
            .Select(p => new CounterPayment(ModeOf(p.Mode), p.Amount, Clean(p.Reference)))
            .ToList();

    private static CounterPaymentMode ModeOf(PaymentMode mode) => mode switch
    {
        PaymentMode.Card => CounterPaymentMode.Card,
        PaymentMode.Upi => CounterPaymentMode.Upi,
        _ => CounterPaymentMode.Cash,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<CounterTaxLine> Tax(IEnumerable<(decimal Rate, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst)> lines) =>
        lines.GroupBy(l => l.Rate)
            .OrderBy(g => g.Key)
            .Select(g => new CounterTaxLine(g.Key, g.Sum(l => l.Taxable), g.Sum(l => l.Cgst), g.Sum(l => l.Sgst), g.Sum(l => l.Igst)))
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
