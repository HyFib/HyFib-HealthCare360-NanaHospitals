using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The steps of the gateway reconciliation job, each run in its own unit of work. Background work has no signed-in
/// facility, so every query here names its facility (or deliberately covers all of them).
/// </summary>
public sealed class GatewayReconciliationRunner(
    HospitalDbContext db,
    PaymentGatewayService payments,
    PrepaymentChecker prepayments,
    GatewayRefundTracker refunds,
    GatewaySettlementService settlements,
    ISettingsService settings,
    TimeProvider time,
    ILogger<GatewayReconciliationRunner> logger)
{
    /// <summary>At most this many records are looked at per step and run; the rest wait for the next run.</summary>
    private const int Batch = 200;

    /// <summary>
    /// Payment links and portal checkouts still waiting for payment (or whose paid receipt could not be recorded): the
    /// gateway is asked again on <see cref="GatewayPolling"/>'s schedule, a paid one is recorded, and a portal checkout
    /// nobody paid within the facility's expiry hours is marked expired.
    /// </summary>
    public async Task<int> RecheckOrdersAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now - GatewayPolling.Window;
        var open = await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(o => (o.Status == GatewayOrderStatus.Created || o.Status == GatewayOrderStatus.Failed) && o.CreatedAt >= since)
            .OrderBy(o => o.CreatedAt)
            .Select(o => new { o.Id, o.CreatedAt })
            .Take(Batch * 4)
            .ToListAsync(ct);
        var expiry = new Dictionary<Guid, int>();
        var checkedCount = 0;
        foreach (var due in open.Where(o => GatewayPolling.IsDue(o.CreatedAt, now)).Take(Batch))
        {
            // Loaded one at a time: a step that fails clears the unit of work, and must not take the others with it.
            var order = await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(o => o.Id == due.Id, ct);
            if (order is not { IsAwaitingPayment: true })
            {
                continue;
            }

            checkedCount++;
            try
            {
                var synced = await payments.SyncAsync(order, ct);
                if (synced.IsSuccess && ReferenceEquals(synced.Value, order) && order is { Kind: GatewayOrderKind.CheckoutOrder, IsAwaitingPayment: true, GatewayPaymentId: null })
                {
                    if (!expiry.TryGetValue(order.FacilityId, out var hours))
                    {
                        hours = (await GatewayReconciliationSettings.ReadAsync(settings, order.FacilityId, ct)).EffectiveCheckoutExpiryHours;
                        expiry[order.FacilityId] = hours;
                    }

                    if (now - order.CreatedAt > TimeSpan.FromHours(hours))
                    {
                        order.MarkExpired($"Nobody paid within {hours} hours.");
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not re-check gateway order {OrderId}", order.OrderId);
                db.ChangeTracker.Clear();
            }
        }

        return checkedCount;
    }

    /// <summary>
    /// Portal bookings whose online payment was started but never confirmed (the patient closed the page after paying):
    /// asked about every run for two hours after the payment started, then hourly while the booking is recent or still
    /// ahead. A paid one is confirmed and its advance receipted.
    /// </summary>
    public async Task<int> RecheckBookingsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var bookedSince = now.AddDays(-3);
        var startsSince = now.AddDays(-1);
        var unpaid = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => a.GatewayOrderId != null && a.PaymentStatus == AppointmentPaymentStatus.Unpaid && (a.BookedAt >= bookedSince || a.StartsAt >= startsSince))
            .OrderByDescending(a => a.BookedAt)
            .Select(a => new { a.Id, StartedAt = a.UpdatedAt ?? a.BookedAt })
            .Take(Batch * 4)
            .ToListAsync(ct);
        var checkedCount = 0;
        foreach (var due in unpaid.Where(a => IsBookingDue(a.StartedAt, now)).Take(Batch))
        {
            var booking = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.Id == due.Id, ct);
            if (booking is not { PaymentStatus: AppointmentPaymentStatus.Unpaid })
            {
                continue;
            }

            checkedCount++;
            try
            {
                await prepayments.ConfirmAsync(booking, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not re-check the online payment of appointment {AppointmentId}", booking.Id);
                db.ChangeTracker.Clear();
            }
        }

        return checkedCount;
    }

    /// <summary>Refunds paid out through the gateway in the last month that the bank has not confirmed yet.</summary>
    public async Task<int> FollowRefundsAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow() - GatewayPolling.Window;
        var pending = await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.GatewayRefundId != null && r.Status == RefundStatus.Paid && r.PaidAt >= since
                        && (r.GatewayStatus == null || r.GatewayStatus == PaymentRefundStatus.Pending))
            .OrderBy(r => r.PaidAt)
            .Select(r => r.Id)
            .Take(Batch)
            .ToListAsync(ct);
        var changed = 0;
        foreach (var id in pending)
        {
            var refund = await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (refund is null)
            {
                continue;
            }

            try
            {
                changed += await refunds.FollowAsync(refund, ct) ? 1 : 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not follow gateway refund {GatewayRefundId}", refund.GatewayRefundId);
                db.ChangeTracker.Clear();
            }
        }

        return changed;
    }

    /// <summary>
    /// Today's and yesterday's settlements for every active facility with reconciliation on, in facility-code order
    /// (so a merchant account shared by several facilities keeps its settlements under the same one), then the
    /// receipts still unsettled after the expected days.
    /// </summary>
    public async Task<int> FetchSettlementsAsync(CancellationToken ct)
    {
        var today = BillingTime.Today(time);
        var facilities = await db.Set<Facility>().AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.Code).Select(f => f.Id).ToListAsync(ct);
        var added = 0;
        foreach (var facilityId in facilities)
        {
            if (!(await GatewayReconciliationSettings.ReadAsync(settings, facilityId, ct)).Enabled)
            {
                continue;
            }

            foreach (var day in new[] { today.AddDays(-1), today })
            {
                try
                {
                    var fetched = await settlements.FetchAsync(facilityId, day, ct);
                    if (fetched.IsFailure)
                    {
                        // Usually "not configured": nothing to reconcile for this facility.
                        logger.LogDebug("Settlements of {Day} not read for facility {FacilityId}: {Reason}", day, facilityId, fetched.Error!.Message);
                        break;
                    }

                    added += fetched.Value.Added;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not store the gateway's settlements of {Day} for facility {FacilityId}", day, facilityId);
                    db.ChangeTracker.Clear();
                }
            }

            try
            {
                await settlements.FindUnsettledReceiptsAsync(facilityId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not look for unsettled online receipts of facility {FacilityId}", facilityId);
                db.ChangeTracker.Clear();
            }
        }

        return added;
    }

    /// <summary>Every run while the payment is fresh (two hours from when it was started), then about hourly.</summary>
    internal static bool IsBookingDue(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var age = now - startedAt;
        return age >= GatewayPolling.Grace && (age < TimeSpan.FromHours(2) || GatewayPolling.IsHourly(now));
    }
}

/// <summary>
/// The gateway reconciliation job (INT-PG): every 5 minutes it asks the gateway about payments still open (links,
/// portal checkouts, prepaid bookings); hourly, and once at start, it follows gateway refunds, reads the settlements of
/// today and yesterday, and lists receipts still unsettled. Runs only on nodes that run jobs
/// (<see cref="PlatformOptions.RunsJobs"/>); every step is idempotent, so a second worker does no harm.
/// </summary>
internal sealed class GatewayReconciliationJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<GatewayReconciliationJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(GatewayPolling.Interval, time);
        var first = true;
        do
        {
            var hourly = first || GatewayPolling.IsHourly(time.GetUtcNow());
            first = false;
            await StepAsync("re-check open online payments", (runner, ct) => runner.RecheckOrdersAsync(ct), stoppingToken);
            await StepAsync("re-check prepaid bookings", (runner, ct) => runner.RecheckBookingsAsync(ct), stoppingToken);
            if (hourly)
            {
                await StepAsync("follow gateway refunds", (runner, ct) => runner.FollowRefundsAsync(ct), stoppingToken);
                await StepAsync("read gateway settlements", (runner, ct) => runner.FetchSettlementsAsync(ct), stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task StepAsync(string name, Func<GatewayReconciliationRunner, CancellationToken, Task<int>> step, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await step(scope.ServiceProvider.GetRequiredService<GatewayReconciliationRunner>(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Gateway reconciliation step '{Step}' failed; it runs again on the next tick", name);
        }
    }
}
