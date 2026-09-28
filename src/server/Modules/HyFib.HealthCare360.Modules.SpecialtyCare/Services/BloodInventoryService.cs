using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>Blood stock by group and component (BB-05) and the expiry housekeeping behind it.</summary>
public sealed class BloodInventoryService(HospitalDbContext db, INotificationService notifications, TimeProvider time)
{
    /// <summary>Units expiring within this window are highlighted so they are issued first or moved to where they will be used.</summary>
    public static readonly TimeSpan ExpiringSoonWindow = TimeSpan.FromDays(3);

    public async Task<BloodInventoryDashboard> DashboardAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var soon = now + ExpiringSoonWindow;
        var inStock = db.Set<BloodComponent>().AsNoTracking()
            .Where(c => c.ExpiresAt > now
                        && (c.Status == BloodUnitStatus.Available || c.Status == BloodUnitStatus.Reserved || c.Status == BloodUnitStatus.Quarantine));

        var cells = await inStock
            .GroupBy(c => new { c.ComponentType, c.BloodGroup })
            .Select(g => new InventoryCell(
                g.Key.ComponentType,
                g.Key.BloodGroup,
                g.Count(c => c.Status == BloodUnitStatus.Available),
                g.Count(c => c.Status == BloodUnitStatus.Reserved),
                g.Count(c => c.Status == BloodUnitStatus.Quarantine),
                g.Count(c => c.Status != BloodUnitStatus.Quarantine && c.ExpiresAt <= soon)))
            .ToListAsync(ct);

        var expiring = await inStock
            .Where(c => c.Status != BloodUnitStatus.Quarantine && c.ExpiresAt <= soon)
            .OrderBy(c => c.ExpiresAt)
            .Select(c => new ExpiringUnit(c.Id, c.UnitNumber, c.ComponentType, c.BloodGroup, c.ExpiresAt, c.Status))
            .Take(200)
            .ToListAsync(ct);

        var openRequests = db.Set<BloodRequest>().AsNoTracking()
            .Where(r => r.Status != BloodRequestStatus.Issued && r.Status != BloodRequestStatus.Cancelled);
        return new BloodInventoryDashboard(
            now,
            cells.OrderBy(c => c.Component).ThenBy(c => c.BloodGroup).ToList(),
            expiring,
            await openRequests.CountAsync(ct),
            await openRequests.CountAsync(r => r.Urgency == BloodRequestUrgency.Emergency, ct),
            cells.Sum(c => c.Quarantine));
    }

    /// <summary>
    /// Marks every in-stock unit past its expiry as expired (all facilities; the job has no signed-in user), releases
    /// their reservations and tells each blood bank how many units expired and how many expire within 3 days.
    /// Issue checks expiry independently, so this job is housekeeping rather than the safety gate.
    /// </summary>
    public async Task<int> MarkExpiredUnitsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.Set<BloodComponent>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.ExpiresAt <= now
                        && (c.Status == BloodUnitStatus.Available || c.Status == BloodUnitStatus.Reserved || c.Status == BloodUnitStatus.Quarantine))
            .ToListAsync(ct);

        var requestIds = due.Where(c => c.Status == BloodUnitStatus.Reserved && c.ReservedForRequestId is not null)
            .Select(c => c.ReservedForRequestId!.Value)
            .ToList();
        var requests = await db.Set<BloodRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => requestIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);

        foreach (var unit in due)
        {
            if (unit.Status == BloodUnitStatus.Reserved && unit.ReservedForRequestId is { } requestId && requests.TryGetValue(requestId, out var request))
            {
                request.OnReservationReleased();
            }

            unit.MarkExpiredIfDue(now);
        }

        await db.SaveChangesAsync(ct);

        var soon = now + ExpiringSoonWindow;
        var expiringByFacility = await db.Set<BloodComponent>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(c => c.ExpiresAt > now && c.ExpiresAt <= soon && (c.Status == BloodUnitStatus.Available || c.Status == BloodUnitStatus.Reserved))
            .GroupBy(c => c.FacilityId)
            .Select(g => new { FacilityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FacilityId, x => x.Count, ct);
        var expiredByFacility = due.GroupBy(c => c.FacilityId).ToDictionary(g => g.Key, g => g.Count());

        foreach (var facilityId in expiredByFacility.Keys.Union(expiringByFacility.Keys))
        {
            var expired = expiredByFacility.GetValueOrDefault(facilityId);
            var expiring = expiringByFacility.GetValueOrDefault(facilityId);
            await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankInventory, facilityId, "Blood stock expiry",
                $"{expired} unit(s) expired today; {expiring} unit(s) expire within 3 days.", "/blood-bank/inventory", "bloodbank",
                expired > 0 ? "warning" : "info", ct);
        }

        return due.Count;
    }
}
