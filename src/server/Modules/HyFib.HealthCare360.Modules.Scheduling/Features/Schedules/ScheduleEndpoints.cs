using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Schedules;

/// <summary>/api/v1/scheduling/schedules and /exceptions — doctor OPD sessions, leave, blocks and extra sessions (PMS-01).</summary>
internal static class ScheduleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/scheduling").WithTags("Scheduling");

        group.MapGet("/schedules", ListSchedules).RequireAnyPermission(SchedulingPermissions.SchedulesManage, SchedulingPermissions.AppointmentsRead);
        group.MapGet("/schedules/{id:guid}", GetSchedule).RequireAnyPermission(SchedulingPermissions.SchedulesManage, SchedulingPermissions.AppointmentsRead);
        group.MapPost("/schedules", CreateSchedules).RequirePermission(SchedulingPermissions.SchedulesManage).WithValidation<CreateSchedulesRequest>();
        group.MapPut("/schedules/{id:guid}", UpdateSchedule).RequirePermission(SchedulingPermissions.SchedulesManage).WithValidation<UpdateScheduleRequest>();
        group.MapDelete("/schedules/{id:guid}", DeleteSchedule).RequirePermission(SchedulingPermissions.SchedulesManage);

        group.MapGet("/exceptions", ListExceptions).RequireAnyPermission(SchedulingPermissions.SchedulesManage, SchedulingPermissions.AppointmentsRead);
        group.MapPost("/exceptions", (SaveScheduleExceptionRequest request, ScheduleService schedules, CancellationToken ct) => SaveException(null, request, schedules, ct))
            .RequirePermission(SchedulingPermissions.SchedulesManage).WithValidation<SaveScheduleExceptionRequest>();
        group.MapPut("/exceptions/{id:guid}", (Guid id, SaveScheduleExceptionRequest request, ScheduleService schedules, CancellationToken ct) => SaveException(id, request, schedules, ct))
            .RequirePermission(SchedulingPermissions.SchedulesManage).WithValidation<SaveScheduleExceptionRequest>();
        group.MapDelete("/exceptions/{id:guid}", DeleteException).RequirePermission(SchedulingPermissions.SchedulesManage);
    }

    private static async Task<IResult> ListSchedules(Guid? practitionerId, Guid? departmentId, bool? activeOnly, HospitalDbContext db, CancellationToken ct)
    {
        var schedules = db.Set<DoctorSchedule>().AsNoTracking();
        if (practitionerId is not null)
        {
            schedules = schedules.Where(s => s.PractitionerId == practitionerId);
        }

        if (departmentId is not null)
        {
            schedules = schedules.Where(s => s.DepartmentId == departmentId);
        }

        if (activeOnly != false)
        {
            schedules = schedules.Where(s => s.IsActive);
        }

        var rows = await Project(db, schedules).Take(1000).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).OrderBy(s => s.PractitionerName).ThenBy(s => s.Weekday).ThenBy(s => s.StartTime).ToList());
    }

    private static async Task<IResult> GetSchedule(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var row = await Project(db, db.Set<DoctorSchedule>().AsNoTracking().Where(s => s.Id == id)).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound(nameof(DoctorSchedule), id).ToProblem() : TypedResults.Ok(ToDto(row));
    }

    private static async Task<IResult> CreateSchedules(CreateSchedulesRequest request, ScheduleService schedules, CancellationToken ct) =>
        (await schedules.CreateAsync(request, ct)).ToCreated(r => $"/api/v1/scheduling/schedules/{r.Ids[0]}");

    private static async Task<IResult> UpdateSchedule(Guid id, UpdateScheduleRequest request, ScheduleService schedules, CancellationToken ct) =>
        (await schedules.UpdateAsync(id, request, ct)).ToOk();

    private static async Task<IResult> DeleteSchedule(Guid id, ScheduleService schedules, CancellationToken ct) =>
        (await schedules.DeleteAsync(id, ct)).ToOk();

    private static async Task<IResult> ListExceptions(Guid? practitionerId, DateOnly? from, DateOnly? to, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var start = from ?? time.Today();
        var end = to ?? start.AddDays(90);
        var facilityId = user.FacilityId;
        var exceptions = db.Set<ScheduleException>().AsNoTracking()
            .Where(e => e.ToDate >= start && e.FromDate <= end && (e.FacilityId == null || facilityId == null || e.FacilityId == facilityId));
        if (practitionerId is not null)
        {
            exceptions = exceptions.Where(e => e.PractitionerId == practitionerId);
        }

        var rows = await (
                from e in exceptions
                join p in db.Set<Practitioner>() on e.PractitionerId equals p.Id into practitioners
                from p in practitioners.DefaultIfEmpty()
                orderby e.FromDate, e.StartTime
                select new { Exception = e, PractitionerName = p != null ? p.DisplayName : null })
            .Take(1000)
            .ToListAsync(ct);

        return TypedResults.Ok(rows.Select(r => new ScheduleExceptionDto(
            r.Exception.Id, r.Exception.PractitionerId, r.PractitionerName ?? "Doctor", r.Exception.FacilityId, r.Exception.Type, r.Exception.FromDate,
            r.Exception.ToDate, r.Exception.IsAllDay, r.Exception.StartTime, r.Exception.EndTime, r.Exception.Reason, r.Exception.DepartmentId,
            r.Exception.SlotMinutes, r.Exception.CapacityPerSlot, r.Exception.Mode, r.Exception.Room, r.Exception.Version)).ToList());
    }

    private static async Task<IResult> SaveException(Guid? id, SaveScheduleExceptionRequest request, ScheduleService schedules, CancellationToken ct)
    {
        var result = await schedules.SaveExceptionAsync(id, request, ct);
        return id is null ? result.ToCreated(r => $"/api/v1/scheduling/exceptions/{r.Id}") : result.ToOk();
    }

    private static async Task<IResult> DeleteException(Guid id, ScheduleService schedules, CancellationToken ct) =>
        (await schedules.DeleteExceptionAsync(id, ct)).ToNoContent();

    private static IQueryable<ScheduleRow> Project(HospitalDbContext db, IQueryable<DoctorSchedule> schedules) =>
        from s in schedules
        join p in db.Set<Practitioner>() on s.PractitionerId equals p.Id into practitioners
        from p in practitioners.DefaultIfEmpty()
        join d in db.Set<Department>() on s.DepartmentId equals d.Id into departments
        from d in departments.DefaultIfEmpty()
        select new ScheduleRow(s, p != null ? p.DisplayName : null, d != null ? d.Name : null);

    private static ScheduleDto ToDto(ScheduleRow row)
    {
        var s = row.Schedule;
        return new ScheduleDto(
            s.Id, s.FacilityId, s.PractitionerId, row.PractitionerName ?? "Doctor", s.DepartmentId, row.DepartmentName, s.Weekday, s.StartTime,
            s.EndTime, s.SlotMinutes, s.CapacityPerSlot, s.SlotsPerSession, s.Mode, s.Room, s.EffectiveFrom, s.EffectiveTo, s.IsActive, s.Notes, s.Version);
    }

    private sealed record ScheduleRow(DoctorSchedule Schedule, string? PractitionerName, string? DepartmentName);
}
