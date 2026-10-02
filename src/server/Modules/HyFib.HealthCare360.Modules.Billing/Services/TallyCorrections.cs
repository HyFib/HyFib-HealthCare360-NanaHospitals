using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Puts a day book back in the Tally queue when something is recorded for a day that already went to Tally: an online
/// payment recorded late (its receipt is dated when the patient paid) or a settlement fetched after its day was
/// posted. Vouchers carry stable numbers, so the day posts again without duplicates. A day not yet queued is left to
/// the nightly post.
/// </summary>
public sealed class TallyCorrections(HospitalDbContext db, TallyService tally, TimeProvider time)
{
    internal async Task RepostIfPostedAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        if (date >= BillingTime.Today(time))
        {
            return;
        }

        var status = await db.Set<TallyExport>().AsNoTracking()
            .Where(e => e.FacilityId == facilityId && e.Date == date)
            .Select(e => (TallyExportStatus?)e.Status)
            .FirstOrDefaultAsync(ct);
        if (status is null or TallyExportStatus.Queued)
        {
            return;
        }

        await tally.QueueAsync(facilityId, date, ct);
    }
}
