using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// The bed classes of a facility's wards in use, for Billing's rate list: each ward's bed charge, with the nursing and
/// consultant-visit charges <see cref="InpatientChargeService"/> posts for a day and a visit there. Wards that share a
/// bed charge and are alike in critical care make one class.
/// </summary>
internal sealed class RoomClassDirectory(HospitalDbContext db) : IRoomClassDirectory
{
    public async Task<IReadOnlyList<RoomClass>> ForFacilityAsync(Guid facilityId, CancellationToken cancellationToken)
    {
        var wards = await db.Set<Ward>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(w => w.FacilityId == facilityId && w.IsActive && w.BedServiceItemId != null)
            .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name)
            .Select(w => new { BedServiceItemId = w.BedServiceItemId!.Value, w.Type, w.Name })
            .ToListAsync(cancellationToken);

        var codes = new[] { InpatientChargeService.NursingServiceCode, ConsultantVisit.ServiceCodeFor(false), ConsultantVisit.ServiceCodeFor(true) };
        var items = await db.Set<ServiceItem>().AsNoTracking()
            .Where(s => codes.Contains(s.Code) && s.IsActive)
            .ToDictionaryAsync(s => s.Code, s => s.Id, cancellationToken);
        Guid? Item(string code) => items.TryGetValue(code, out var id) ? id : null;

        return
        [
            .. wards
                .GroupBy(w => (w.BedServiceItemId, IntensiveCare: Ward.IsCriticalCareType(w.Type)))
                .Select(g => new RoomClass(
                    g.Key.BedServiceItemId,
                    g.Key.IntensiveCare,
                    Item(InpatientChargeService.NursingServiceCode),
                    Item(ConsultantVisit.ServiceCodeFor(g.Key.IntensiveCare)),
                    [.. g.Select(w => w.Name)])),
        ];
    }
}
