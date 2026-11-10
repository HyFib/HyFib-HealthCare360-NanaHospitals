using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Downtime numbers voided unused, one by one or with their returned block, by the day voided, each with the key of the
/// series it was reserved from: GSTR-1 table 13 counts the bill numbers among them as cancelled.
/// </summary>
internal sealed class DowntimeVoidedNumbers(HospitalDbContext db, IEnumerable<DowntimeNumberSeries> series) : IVoidedDocumentNumbers
{
    public async Task<IReadOnlyList<VoidedDocumentNumber>> VoidedAsync(IReadOnlyCollection<Guid> facilityIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var keys = series.ToDictionary(s => s.Kind, s => s.Series.Key);
        // A day either side in UTC, then the hospital's calendar day of each.
        var start = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var voided = await (
                from n in db.Set<DowntimeNumber>().AsNoTracking()
                join b in db.Set<DowntimeNumberBlock>().AsNoTracking() on n.BlockId equals b.Id
                where facilityIds.Contains(b.FacilityId) && n.EntityId == null && n.VoidedAt >= start && n.VoidedAt < end
                select new { b.Kind, n.Number, n.VoidedAt })
            .ToListAsync(cancellationToken);
        return voided
            .Select(v => (v.Kind, v.Number, On: DateOnly.FromDateTime(NumberFormat.ToHospitalTime(v.VoidedAt!.Value).DateTime)))
            .Where(v => keys.ContainsKey(v.Kind) && v.On >= from && v.On <= to)
            .Select(v => new VoidedDocumentNumber(keys[v.Kind], v.Number, v.On))
            .ToList();
    }
}
