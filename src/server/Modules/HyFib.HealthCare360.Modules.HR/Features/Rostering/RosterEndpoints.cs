using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.Rostering;

/// <summary>/api/v1/hr/shifts and /api/v1/hr/roster — shift master and duty roster (HR-03).</summary>
internal static class RosterEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr").WithTags("HR");

        group.MapGet("/shifts", ListShifts).RequireAnyPermission(HRPermissions.RosterManage, HRPermissions.AttendanceManage, HRPermissions.EmployeesRead);
        group.MapPost("/shifts", (SaveShiftRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveShift(null, r, db, u, ct))
            .RequirePermission(HRPermissions.RosterManage).WithValidation<SaveShiftRequest>();
        group.MapPut("/shifts/{id:guid}", (Guid id, SaveShiftRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveShift(id, r, db, u, ct))
            .RequirePermission(HRPermissions.RosterManage).WithValidation<SaveShiftRequest>().RequireVersion();

        group.MapGet("/roster", GetRoster).RequireAnyPermission(HRPermissions.RosterManage, HRPermissions.AttendanceManage);
        group.MapPost("/roster/check", async (SaveRosterRequest r, RosterService roster, CancellationToken ct) => TypedResults.Ok(await roster.CheckAsync(r, ct)))
            .RequirePermission(HRPermissions.RosterManage).WithValidation<SaveRosterRequest>();
        group.MapPut("/roster", async (SaveRosterRequest r, RosterService roster, CancellationToken ct) => (await roster.SaveAsync(r, checkVersions: true, ct)).ToOk())
            .RequirePermission(HRPermissions.RosterManage).WithValidation<SaveRosterRequest>();
        group.MapPost("/roster/copy-week", async (CopyRosterWeekRequest r, RosterService roster, CancellationToken ct) => (await roster.CopyWeekAsync(r, ct)).ToOk())
            .RequirePermission(HRPermissions.RosterManage).WithValidation<CopyRosterWeekRequest>();
        group.MapDelete("/roster/{id:guid}", DeleteEntry).RequirePermission(HRPermissions.RosterManage);
    }

    private static async Task<IResult> ListShifts(bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var shifts = await db.Set<Shift>().AsNoTracking()
            .Where(s => includeInactive == true || s.IsActive)
            .OrderBy(s => s.StartTime).ThenBy(s => s.Code)
            .ToListAsync(ct);
        return TypedResults.Ok(shifts.Select(ToDto).ToList());
    }

    private static async Task<IResult> SaveShift(Guid? id, SaveShiftRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Shift>().AnyAsync(s => s.Code == code && s.Id != id, ct))
        {
            return Error.Conflict("shift.code_in_use", $"Shift code {code} is already used.").ToProblem();
        }

        Shift shift;
        if (id is { } existingId)
        {
            var found = await db.Set<Shift>().FirstOrDefaultAsync(s => s.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Shift), existingId).ToProblem();
            }

            shift = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(shift, version);
            }
        }
        else
        {
            if (user.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.").ToProblem();
            }

            shift = new Shift { FacilityId = facilityId };
            db.Add(shift);
        }

        var net = new ShiftTimes(request.StartTime, request.EndTime, request.BreakMinutes, 0, 0, 0, 0).NetMinutes;
        shift.Code = code;
        shift.Name = request.Name.Trim();
        shift.StartTime = request.StartTime;
        shift.EndTime = request.EndTime;
        shift.BreakMinutes = request.BreakMinutes;
        shift.GraceMinutes = request.GraceMinutes;
        shift.IsNightShift = request.IsNightShift;
        shift.FullDayMinutes = request.FullDayMinutes ?? Math.Max(30, net - 30);
        shift.HalfDayMinutes = request.HalfDayMinutes ?? Math.Max(30, net / 2);
        shift.OvertimeThresholdMinutes = request.OvertimeThresholdMinutes;
        shift.Color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.ToUpperInvariant();
        shift.IsActive = request.IsActive;

        if (shift.HalfDayMinutes >= shift.FullDayMinutes)
        {
            return Error.Field("halfDayMinutes", "A half day must be shorter than a full day.").ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/hr/shifts/{shift.Id}", new { shift.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> GetRoster(DateOnly from, DateOnly to, Guid? departmentId, Guid? employeeId, HospitalDbContext db, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 62)
        {
            return Error.Field("to", "Choose a range of at most 62 days.").ToProblem();
        }

        var rows = await (
                from r in db.Set<RosterEntry>().AsNoTracking()
                join e in db.Set<Employee>() on r.EmployeeId equals e.Id
                join s in db.Set<Shift>() on r.ShiftId equals s.Id into shifts
                from s in shifts.DefaultIfEmpty()
                where r.Date >= @from && r.Date <= to
                      && (departmentId == null || e.DepartmentId == departmentId)
                      && (employeeId == null || r.EmployeeId == employeeId)
                orderby e.FullName, r.Date, s != null ? s.StartTime : TimeOnly.MinValue
                select new RosterEntryDto(
                    r.Id, r.EmployeeId, e.EmployeeCode, e.FullName, r.Date, r.ShiftId, s != null ? s.Code : null, s != null ? s.Name : null,
                    s != null ? s.Color : null, s != null ? (TimeOnly?)s.StartTime : null, s != null ? (TimeOnly?)s.EndTime : null, r.IsWeekOff, r.Notes,
                    r.Version))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> DeleteEntry(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var entry = await db.Set<RosterEntry>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entry is null)
        {
            return Error.NotFound(nameof(RosterEntry), id).ToProblem();
        }

        db.Remove(entry);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    internal static ShiftDto ToDto(Shift s) => new(
        s.Id, s.Code, s.Name, s.StartTime, s.EndTime, s.BreakMinutes, s.GraceMinutes, s.IsNightShift, s.HalfDayMinutes, s.FullDayMinutes,
        s.OvertimeThresholdMinutes, s.ToTimes().DurationMinutes, s.Color, s.IsActive, s.Version);
}
