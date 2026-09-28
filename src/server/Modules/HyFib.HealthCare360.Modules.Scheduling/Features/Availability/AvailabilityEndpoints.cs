using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Availability;

/// <summary>/api/v1/scheduling/slots — live slot availability for the front desk (PMS-01/02).</summary>
internal static class AvailabilityEndpoints
{
    private const int MaxDays = 31;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/scheduling").WithTags("Scheduling")
            .MapGet("/slots", Slots)
            .RequireAnyPermission(SchedulingPermissions.AppointmentsRead, SchedulingPermissions.AppointmentsBook);
    }

    /// <summary>
    /// Slots of one doctor, or of every doctor with sessions in a department, between two local dates. Full slots are
    /// included by default (with their booking count) because the desk may overbook or look for cancellations.
    /// </summary>
    private static async Task<IResult> Slots(
        Guid? practitionerId,
        Guid? departmentId,
        DateOnly? from,
        DateOnly? to,
        ConsultationMode? mode,
        bool? includeFull,
        HospitalDbContext db,
        AvailabilityService availability,
        TimeProvider time,
        CancellationToken ct)
    {
        if (practitionerId is null && departmentId is null)
        {
            return Error.Field("practitionerId", "Choose a doctor or a department.").ToProblem();
        }

        var start = from ?? time.Today();
        var end = to ?? start;
        if (end < start || end.DayNumber - start.DayNumber >= MaxDays)
        {
            return Error.Field("to", $"Choose a range of 1 to {MaxDays} days.").ToProblem();
        }

        List<Guid> ids = practitionerId is { } single
            ? [single]
            : await db.Set<DoctorSchedule>().AsNoTracking()
                .Where(s => s.DepartmentId == departmentId && s.IsActive)
                .Select(s => s.PractitionerId)
                .Distinct()
                .ToListAsync(ct);

        var query = new SlotQuery(start, end, time.GetUtcNow()) { Mode = mode, IncludeFull = includeFull ?? true };
        var slots = await availability.ComputeAsync(ids, query, ct);

        var doctors = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.DisplayName, p.DepartmentId, p.AvailableForTelehealth })
            .ToListAsync(ct);
        var departmentIds = doctors.Where(d => d.DepartmentId != null).Select(d => d.DepartmentId!.Value).Distinct().ToList();
        var departments = await db.Set<Department>().AsNoTracking()
            .Where(d => departmentIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var result = doctors
            .Select(d =>
            {
                var mine = (slots.GetValueOrDefault(d.Id) ?? [])
                    .Where(s => departmentId is null || s.DepartmentId == departmentId)
                    .Select(SlotDto.From)
                    .ToList();
                return new PractitionerAvailability(
                    d.Id, d.DisplayName, d.DepartmentId, d.DepartmentId is { } dep ? departments.GetValueOrDefault(dep) : null,
                    d.AvailableForTelehealth, mine.Count(s => s.Available > 0), mine);
            })
            .OrderBy(a => a.PractitionerName)
            .ToList();

        return TypedResults.Ok(result);
    }
}
