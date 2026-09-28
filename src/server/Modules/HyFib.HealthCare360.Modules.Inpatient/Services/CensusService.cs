using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Census;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Occupancy and census statistics for a period (WB-06; feeds MIS bed occupancy, ALOS, admissions and discharges).
/// Loads only the admissions overlapping the period and their bed history, then applies the pure
/// <see cref="CensusCalculator"/> hospital-wide and per ward.
/// </summary>
public sealed class CensusService(HospitalDbContext db)
{
    public const int MaxDays = 93;

    public async Task<CensusReport> ReportAsync(DateOnly from, DateOnly to, Guid? wardId, CancellationToken ct)
    {
        var start = IndiaClock.StartOfDayUtc(from);
        var end = IndiaClock.StartOfDayUtc(to.AddDays(1));

        var admissions = await db.Set<Admission>().AsNoTracking()
            .Where(a => a.Status != AdmissionStatus.Cancelled && a.AdmittedAt < end && (a.DischargedAt == null || a.DischargedAt >= start))
            .Select(a => new { a.Id, a.AdmittedAt, a.DischargedAt, a.Status })
            .ToListAsync(ct);
        var ids = admissions.Select(a => a.Id).ToList();
        var stays = await db.Set<BedAssignment>().AsNoTracking()
            .Where(s => ids.Contains(s.AdmissionId))
            .Select(s => new { s.AdmissionId, s.FromAt, s.ToAt, s.BedServiceItemId, s.WardId })
            .ToListAsync(ct);
        var staysByAdmission = stays
            .GroupBy(s => s.AdmissionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BedStay>)g.OrderBy(s => s.FromAt).Select(s => new BedStay(s.FromAt, s.ToAt, s.BedServiceItemId, s.WardId)).ToList());
        var records = admissions
            .Select(a => new StayRecord(a.Id, a.AdmittedAt, a.DischargedAt, a.Status, staysByAdmission.GetValueOrDefault(a.Id) ?? []))
            .ToList();

        var wards = await db.Set<Ward>().AsNoTracking()
            .Where(w => w.IsActive && (wardId == null || w.Id == wardId))
            .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name)
            .Select(w => new { w.Id, w.Code, w.Name, w.Type })
            .ToListAsync(ct);
        var bedsInService = await db.Set<Bed>().AsNoTracking()
            .Where(b => b.IsActive && b.Status != BedStatus.Blocked && b.Status != BedStatus.Maintenance)
            .GroupBy(b => b.WardId)
            .Select(g => new { WardId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WardId, x => x.Count, ct);
        var totalBeds = wardId is { } id ? bedsInService.GetValueOrDefault(id) : bedsInService.Values.Sum();

        var hospital = CensusCalculator.Summarize(records, from, to, totalBeds, wardId);
        var byWard = wards
            .Select(w => new WardCensus(w.Id, w.Code, w.Name, w.Type, bedsInService.GetValueOrDefault(w.Id),
                CensusCalculator.Summarize(records, from, to, bedsInService.GetValueOrDefault(w.Id), w.Id)))
            .ToList();

        var turnarounds = await db.Set<HousekeepingTask>().AsNoTracking()
            .Where(t => t.Status == HousekeepingStatus.Completed && t.CompletedAt >= start && t.CompletedAt < end
                        && t.TurnaroundMinutes != null && (wardId == null || t.WardId == wardId))
            .Select(t => t.TurnaroundMinutes!.Value)
            .ToListAsync(ct);

        return new CensusReport(from, to, totalBeds, hospital, byWard, turnarounds.Count,
            turnarounds.Count == 0 ? null : Math.Round(turnarounds.Average(), 1, MidpointRounding.AwayFromZero));
    }
}
