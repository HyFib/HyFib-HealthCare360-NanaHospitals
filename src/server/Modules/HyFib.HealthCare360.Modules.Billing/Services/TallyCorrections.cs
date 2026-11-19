using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>An entry saved with the date of an earlier day (<see cref="TallyCorrections.LateEntryMessage"/>): a paper bill, a receipt from the manual receipt book or a pharmacy sale entered from paper.</summary>
internal sealed record LateEntry(Guid FacilityId, DateOnly Date, string Source, string? Reference);

/// <summary>A goods receipt posted into stock after the day its goods were received (<see cref="TallyCorrections.LatePurchaseMessage"/>).</summary>
internal sealed record LatePurchase(Guid FacilityId, DateOnly ReceivedOn, string Number);

/// <summary>
/// Puts a day book back in the Tally queue when something is recorded for a day that already went to Tally: an online
/// payment recorded late (its receipt is dated when the patient paid), a settlement fetched after its day was posted, a
/// goods receipt posted into stock after the day its goods came (its purchase is dated by that day), or a paper entry (a
/// bill, a receipt from the manual receipt book, a pharmacy sale) saved after the day its paper is dated. Vouchers carry
/// stable numbers, so the day posts again without duplicates. A day not yet queued is left to the nightly post.
/// </summary>
public sealed class TallyCorrections(HospitalDbContext db, TallyService tally, TimeProvider time)
{
    /// <summary>
    /// The outbox message a goods receipt of an earlier day queues when it is posted (<see cref="LatePurchase"/>), in
    /// Supply chain's transaction; its day is looked at once that has committed.
    /// </summary>
    public const string LatePurchaseMessage = "billing.tally.late-purchase";

    /// <summary>
    /// The outbox message an entry dated in an earlier day queues when it is saved (<see cref="LateEntry"/>), in the
    /// transaction that saves it; its day is looked at once that has committed.
    /// </summary>
    public const string LateEntryMessage = "billing.tally.late-entry";

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

        await tally.QueueAsync(facilityId, date, date, ct);
    }

    /// <summary>
    /// A goods receipt posted after its day: that day goes to Tally again when it went already and the facility posts the
    /// receipt as a purchase (<see cref="TallyLedgerMap.PurchasesFrom"/>). A receipt into a store whose purchases stay
    /// out of the day book leaves the day as it was sent.
    /// </summary>
    internal async Task RepostForLatePurchaseAsync(LatePurchase purchase, CancellationToken ct)
    {
        if (await tally.PostsPurchaseAsync(purchase.FacilityId, purchase.ReceivedOn, purchase.Number, ct))
        {
            await RepostIfPostedAsync(purchase.FacilityId, purchase.ReceivedOn, ct);
        }
    }
}
