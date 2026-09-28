using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Dashboard;

public sealed record SalesByMode(PaymentMode Mode, decimal Amount, int Count);

/// <summary>Pharmacist's home dashboard (HMS-02): pending work, today's takings by mode and expiry exposure.</summary>
public sealed record PharmacyDashboard(
    int PendingOutpatient,
    int PendingInpatient,
    int PartiallyDispensed,
    int PendingWardIndents,
    int PendingRefills,
    int SalesToday,
    decimal SalesAmountToday,
    decimal RefundsToday,
    IReadOnlyList<SalesByMode> ByMode,
    int NearExpiryBatches,
    decimal NearExpiryValueAtMrp,
    IReadOnlyList<ExpiringBatch> NearExpiry,
    double? AverageWaitMinutes);

/// <summary>/api/v1/pharmacy/dashboard — pending queue, today's sales by payment mode and near-expiry stock in pharmacy stores.</summary>
internal static class DashboardEndpoints
{
    private const int NearExpiryDays = 90;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/pharmacy/dashboard", Dashboard)
            .WithTags("Pharmacy")
            .RequireAnyPermission(PharmacyPermissions.QueueRead, PharmacyPermissions.Sales, PharmacyPermissions.IpIssue);

    private static async Task<IResult> Dashboard(Guid? storeId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = time.Today();
        var (start, end) = HospitalClock.Range(today, today);

        var open = await db.Set<PharmacyQueueEntry>().AsNoTracking()
            .Where(e => e.Status == QueueEntryStatus.Pending || e.Status == QueueEntryStatus.PartiallyDispensed)
            .GroupBy(e => new { e.IsInpatient, e.Status })
            .Select(g => new { g.Key.IsInpatient, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        var wardIndents = await db.Set<WardIndent>().CountAsync(i => i.Status == WardIndentStatus.Pending || i.Status == WardIndentStatus.PartiallyIssued, ct);
        var refills = await db.Set<RefillRequest>().CountAsync(r => r.Status == RefillStatus.Requested, ct);

        var sales = db.Set<PharmacySale>().AsNoTracking().Where(s => s.SoldAt >= start && s.SoldAt < end && s.Status != SaleStatus.Cancelled);
        if (storeId is not null)
        {
            sales = sales.Where(s => s.StoreId == storeId);
        }

        var salesCount = await sales.CountAsync(ct);
        var salesAmount = await sales.SumAsync(s => s.NetAmount, ct);
        var payments = await (
                from p in db.Set<PharmacyPayment>().AsNoTracking()
                join s in db.Set<PharmacySale>() on p.PharmacySaleId equals s.Id
                where p.ReceivedAt >= start && p.ReceivedAt < end && (storeId == null || s.StoreId == storeId)
                group p by new { p.Mode, p.IsRefund } into g
                select new { g.Key.Mode, g.Key.IsRefund, Amount = g.Sum(x => x.Amount), Count = g.Count() })
            .ToListAsync(ct);

        var dispensedToday = await db.Set<PharmacyQueueEntry>().AsNoTracking()
            .Where(e => e.LastDispensedAt >= start && e.LastDispensedAt < end && !e.IsInpatient)
            .Select(e => new { e.ReceivedAt, e.LastDispensedAt })
            .ToListAsync(ct);
        var waits = dispensedToday.Where(e => e.LastDispensedAt is not null).Select(e => (e.LastDispensedAt!.Value - e.ReceivedAt).TotalMinutes).ToList();

        var pharmacyStores = storeId is { } store
            ? [store]
            : await db.Set<Store>().AsNoTracking().Where(s => s.Type == StoreType.Pharmacy && s.IsActive).Select(s => s.Id).ToListAsync(ct);
        var expiring = await InventoryQueries.ExpiringAsync(db, pharmacyStores, today, NearExpiryDays, includeExpired: false, ct);

        return TypedResults.Ok(new PharmacyDashboard(
            open.Where(o => !o.IsInpatient && o.Status == QueueEntryStatus.Pending).Sum(o => o.Count),
            open.Where(o => o.IsInpatient && o.Status == QueueEntryStatus.Pending).Sum(o => o.Count),
            open.Where(o => o.Status == QueueEntryStatus.PartiallyDispensed).Sum(o => o.Count),
            wardIndents,
            refills,
            salesCount,
            salesAmount,
            payments.Where(p => p.IsRefund).Sum(p => p.Amount),
            payments.Where(p => !p.IsRefund).Select(p => new SalesByMode(p.Mode, p.Amount, p.Count)).OrderBy(p => p.Mode).ToList(),
            expiring.Count,
            expiring.Sum(e => e.ValueAtMrp),
            expiring.Take(10).ToList(),
            waits.Count == 0 ? null : Math.Round(waits.Average(), 1)));
    }
}
