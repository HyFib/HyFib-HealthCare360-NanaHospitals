using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>The cash patients paid at the billing counters, for the cash limit (<see cref="CashLimit"/>, s.269ST).</summary>
internal sealed class BillingCashReceipts(HospitalDbContext db) : ICashReceiptSource
{
    /// <summary>Money not received through a bank as s.269ST allows: cash, and wallets, which rule 6ABBA does not list.</summary>
    internal static readonly PaymentMode[] CashModes = [PaymentMode.Cash, PaymentMode.Wallet];

    public Task<decimal> CashReceivedAsync(Guid patientId, IReadOnlyCollection<Guid> facilityIds, DateTimeOffset since, CancellationToken ct) =>
        CashTakenAsync(db, r => r.PatientId == patientId && facilityIds.Contains(r.FacilityId) && r.ReceivedAt >= since, ct);

    /// <summary>The cash on the receipts <paramref name="taken"/> picks, at whichever hospital they were taken.</summary>
    internal static Task<decimal> CashTakenAsync(HospitalDbContext db, Expression<Func<Receipt, bool>> taken, CancellationToken ct) =>
        db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(taken)
            .SelectMany(r => r.Payments).Where(p => CashModes.Contains(p.Mode))
            .SumAsync(p => p.Amount, ct);

    /// <summary>The cash in a payment about to be taken.</summary>
    internal static decimal CashIn(IEnumerable<PaymentLineData> lines) => lines.Where(l => CashModes.Contains(l.Mode)).Sum(l => l.Amount);
}
