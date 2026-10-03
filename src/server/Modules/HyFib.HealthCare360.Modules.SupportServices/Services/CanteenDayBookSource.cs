using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// The canteen's day for Billing's Tally day book (INT-TALLY), through the Platform contract
/// <see cref="ICounterSalesSource"/>.
/// </summary>
internal sealed class CanteenDayBookSource(HospitalDbContext db) : ICounterSalesSource
{
    public const string Key = "canteen";

    public string Counter => Key;

    public async Task<CounterSalesDay> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken)
    {
        var sales = await db.Set<CanteenSale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(s => s.Lines)
            .Where(s => s.FacilityId == facilityId && s.BusinessDate == date && s.Status == CanteenSaleStatus.Completed)
            .ToListAsync(cancellationToken);
        return CanteenDayBook.Day(date, sales);
    }
}

/// <summary>
/// Summarises the canteen's day for the accounts (pure, unit-tested): the day's completed bills at their GST rates, the
/// money taken by mode, and staff credit as what went on account (recovered from salaries by HR). Cancelled bills (same
/// day only) are left out. Staff repayments and payroll deductions are not here: the canteen does not record how a
/// repayment was paid, and payroll is posted with the salaries.
/// </summary>
internal static class CanteenDayBook
{
    public static CounterSalesDay Day(DateOnly date, IReadOnlyList<CanteenSale> sales)
    {
        var completed = sales.Where(s => s.Status == CanteenSaleStatus.Completed).ToList();
        var tax = completed.SelectMany(s => s.Lines)
            .GroupBy(l => l.GstRate)
            .OrderBy(g => g.Key)
            .Select(g => new CounterTaxLine(g.Key, g.Sum(l => l.TaxableAmount), g.Sum(l => l.CgstAmount), g.Sum(l => l.SgstAmount)))
            .ToList();
        var money = completed.Where(s => s.PaymentMode != CanteenPaymentMode.StaffCredit)
            .GroupBy(s => s.PaymentMode)
            .OrderBy(g => g.Key)
            .Select(g => new CounterMoney(g.Key switch
            {
                CanteenPaymentMode.Card => CounterPaymentMode.Card,
                CanteenPaymentMode.Upi => CounterPaymentMode.Upi,
                _ => CounterPaymentMode.Cash,
            }, g.Sum(s => s.NetAmount)))
            .ToList();
        return new CounterSalesDay(CanteenDayBookSource.Key, date, new CounterDocuments(completed.Count, tax, completed.Sum(s => s.RoundOff), money), CounterDocuments.None, []);
    }
}
