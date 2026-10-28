using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// The cash patients paid at the pharmacy counter, for the cash limit (<see cref="CashLimit"/>, s.269ST): at the sale and
/// when a balance is collected later. Money paid back on a return or a cancellation is not taken off.
/// </summary>
internal sealed class PharmacyCashReceipts(HospitalDbContext db) : ICashReceiptSource
{
    public Task<decimal> CashReceivedAsync(
        Guid patientId, IReadOnlyCollection<Guid> facilityIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        db.Set<PharmacySale>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.PatientId == patientId && facilityIds.Contains(s.FacilityId))
            .SelectMany(s => s.Payments)
            .Where(p => p.Mode == PaymentMode.Cash && !p.IsRefund && p.ReceivedAt >= from && p.ReceivedAt < to)
            .SumAsync(p => p.Amount, ct);

    /// <summary>The cash taken for one invoice so far.</summary>
    internal static Task<decimal> CashForSaleAsync(HospitalDbContext db, Guid saleId, CancellationToken ct) =>
        db.Set<PharmacyPayment>().Where(p => p.PharmacySaleId == saleId && p.Mode == PaymentMode.Cash && !p.IsRefund).SumAsync(p => p.Amount, ct);

    /// <summary>The cash in a payment about to be taken.</summary>
    internal static decimal CashIn(IEnumerable<SalePayment> payments) => payments.Where(p => p.Mode == PaymentMode.Cash).Sum(p => p.Amount);
}
