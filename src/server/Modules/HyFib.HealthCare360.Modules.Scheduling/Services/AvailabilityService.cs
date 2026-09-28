using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// Loads what the <see cref="SlotEngine"/> needs (schedules, exceptions, busy intervals) for a set of practitioners in
/// three queries and runs the engine per practitioner. Schedules follow the caller's facility scope (staff see their
/// branch; anonymous and portal callers see every branch). Busy intervals ignore the facility filter on purpose: a
/// doctor's time is taken whichever branch the booking belongs to.
/// </summary>
public sealed class AvailabilityService(HospitalDbContext db)
{
    public HospitalCalendar Calendar { get; } = HospitalCalendar.India;

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Slot>>> ComputeAsync(
        IReadOnlyCollection<Guid> practitionerIds, SlotQuery query, CancellationToken ct)
    {
        var ids = practitionerIds.Distinct().ToList();
        if (ids.Count == 0 || query.To < query.From)
        {
            return new Dictionary<Guid, IReadOnlyList<Slot>>();
        }

        var schedules = await db.Set<DoctorSchedule>().AsNoTracking()
            .Where(s => ids.Contains(s.PractitionerId) && s.IsActive && s.EffectiveFrom <= query.To && (s.EffectiveTo == null || s.EffectiveTo >= query.From))
            .ToListAsync(ct);

        var exceptions = await db.Set<ScheduleException>().AsNoTracking()
            .Where(e => ids.Contains(e.PractitionerId) && e.FromDate <= query.To && e.ToDate >= query.From)
            .ToListAsync(ct);

        var rangeStart = Calendar.StartOfDay(query.From);
        var rangeEnd = Calendar.StartOfDay(query.To.AddDays(1));
        var busy = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(Appointment.HoldsSeatExpression)
            .Where(a => ids.Contains(a.PractitionerId) && a.StartsAt < rangeEnd && a.EndsAt > rangeStart)
            .Select(a => new { a.PractitionerId, a.StartsAt, a.EndsAt })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, IReadOnlyList<Slot>>();
        foreach (var id in ids)
        {
            result[id] = SlotEngine.Compute(
                schedules.Where(s => s.PractitionerId == id).ToList(),
                exceptions.Where(e => e.PractitionerId == id).ToList(),
                busy.Where(b => b.PractitionerId == id).Select(b => new BusyInterval(b.StartsAt, b.EndsAt)).ToList(),
                query,
                Calendar);
        }

        return result;
    }

    public async Task<IReadOnlyList<Slot>> ComputeAsync(Guid practitionerId, SlotQuery query, CancellationToken ct) =>
        (await ComputeAsync([practitionerId], query, ct)).GetValueOrDefault(practitionerId) ?? [];
}
