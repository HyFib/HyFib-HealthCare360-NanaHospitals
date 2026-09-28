using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.Attendance;

/// <summary>/api/v1/hr/attendance — daily attendance, monthly summary, punches and regularisations (HR-04).</summary>
internal static class AttendanceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr/attendance").WithTags("HR");

        group.MapGet("/", List).RequirePermission(HRPermissions.AttendanceManage);
        group.MapGet("/summary", Summary).RequirePermission(HRPermissions.AttendanceManage);
        group.MapPost("/compute", Compute).RequirePermission(HRPermissions.AttendanceManage).WithValidation<ComputeAttendanceRequest>();
        group.MapGet("/punches", Punches).RequirePermission(HRPermissions.AttendanceManage);
        group.MapGet("/unmatched-pins", UnmatchedPins).RequirePermission(HRPermissions.AttendanceManage);

        group.MapGet("/regularisations", ListRegularisations).RequirePermission(HRPermissions.AttendanceManage);
        group.MapPost("/regularisations/{id:guid}/approve", async (Guid id, DecisionRequest r, RegularisationService service, CancellationToken ct) =>
                (await service.DecideAsync(id, approve: true, r.Note, ct)).ToNoContent())
            .RequirePermission(HRPermissions.AttendanceManage).WithValidation<DecisionRequest>();
        group.MapPost("/regularisations/{id:guid}/reject", async (Guid id, DecisionRequest r, RegularisationService service, CancellationToken ct) =>
                (await service.DecideAsync(id, approve: false, r.Note, ct)).ToNoContent())
            .RequirePermission(HRPermissions.AttendanceManage).WithValidation<DecisionRequest>();

        app.MapPost("/api/v1/hr/employees/{employeeId:guid}/regularisations", async (Guid employeeId, RequestRegularisationRequest r, RegularisationService service, CancellationToken ct) =>
                (await service.RequestAsync(employeeId, r, ct)).ToCreated(id => $"/api/v1/hr/attendance/regularisations/{id}"))
            .WithTags("HR").RequirePermission(HRPermissions.AttendanceManage).WithValidation<RequestRegularisationRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        Guid? employeeId,
        Guid? departmentId,
        AttendanceStatus? status,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var end = to ?? time.LocalToday();
        var start = from ?? end;
        var rows = from a in db.Set<AttendanceDay>().AsNoTracking()
                   join e in db.Set<Employee>() on a.EmployeeId equals e.Id
                   join s in db.Set<Shift>() on a.ShiftId equals s.Id into shifts
                   from s in shifts.DefaultIfEmpty()
                   where a.Date >= start && a.Date <= end
                         && (employeeId == null || a.EmployeeId == employeeId)
                         && (departmentId == null || e.DepartmentId == departmentId)
                         && (status == null || a.Status == status)
                   orderby a.Date descending, e.FullName
                   select new AttendanceDayDto(
                       a.Id, a.EmployeeId, e.EmployeeCode, e.FullName, a.Date, a.Status, s != null ? s.Code : null, a.FirstIn, a.LastOut,
                       a.WorkedMinutes, a.LateMinutes, a.EarlyExitMinutes, a.OvertimeMinutes, a.PresentDays, a.PaidLeaveDays, a.UnpaidLeaveDays,
                       a.AbsentDays, a.OffDays, a.Flags, a.PunchCount, a.IsLocked);
        return TypedResults.Ok(await rows.ToPagedResultAsync(page, ct));
    }

    /// <summary>Month-at-a-glance per employee: the sheet HR checks before running payroll.</summary>
    private static async Task<IResult> Summary(int year, int month, Guid? departmentId, HospitalDbContext db, CancellationToken ct)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
        {
            return Error.Field("month", "Choose a valid month.").ToProblem();
        }

        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var days = await (
                from a in db.Set<AttendanceDay>().AsNoTracking()
                join e in db.Set<Employee>() on a.EmployeeId equals e.Id
                where a.Date >= start && a.Date <= end && (departmentId == null || e.DepartmentId == departmentId)
                select new
                {
                    a.EmployeeId, e.EmployeeCode, e.FullName, e.DepartmentId, a.PresentDays, a.PaidLeaveDays, a.UnpaidLeaveDays, a.AbsentDays,
                    a.OffDays, a.LateMinutes, a.OvertimeMinutes, a.Flags,
                })
            .ToListAsync(ct);
        var departments = await db.Set<Department>().AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var summary = days.GroupBy(d => new { d.EmployeeId, d.EmployeeCode, d.FullName, d.DepartmentId })
            .Select(g => new AttendanceSummaryRow(
                g.Key.EmployeeId, g.Key.EmployeeCode, g.Key.FullName,
                g.Key.DepartmentId is { } departmentKey ? departments.GetValueOrDefault(departmentKey) : null,
                g.Count(), g.Sum(d => d.PresentDays), g.Sum(d => d.PaidLeaveDays), g.Sum(d => d.UnpaidLeaveDays), g.Sum(d => d.AbsentDays),
                g.Sum(d => d.OffDays), g.Count(d => d.Flags.HasFlag(AttendanceFlags.Late)), g.Sum(d => d.LateMinutes), g.Sum(d => d.OvertimeMinutes),
                g.Count(d => d.Flags.HasFlag(AttendanceFlags.MissingPunch))))
            .OrderBy(r => r.EmployeeName)
            .ToList();
        return TypedResults.Ok(summary);
    }

    private static async Task<IResult> Compute(ComputeAttendanceRequest request, AttendanceService attendance, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var written = await attendance.ComputeAsync(facilityId, request.From, request.To, request.EmployeeId is { } id ? [id] : null, ct);
        return TypedResults.Ok(new ComputeAttendanceResult(written));
    }

    private static async Task<IResult> Punches(Guid? employeeId, DateOnly from, DateOnly to, HospitalDbContext db, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 31)
        {
            return Error.Field("to", "Choose a range of at most 31 days.").ToProblem();
        }

        var start = HrClock.StartOfDayUtc(from);
        var end = HrClock.StartOfDayUtc(to.AddDays(1));
        var punches = await (
                from p in db.Set<AttendancePunch>().AsNoTracking()
                join e in db.Set<Employee>() on p.EmployeeId equals e.Id into employees
                from e in employees.DefaultIfEmpty()
                join d in db.Set<BiometricDevice>() on p.BiometricDeviceId equals d.Id into devices
                from d in devices.DefaultIfEmpty()
                where p.PunchedAt >= start && p.PunchedAt < end && (employeeId == null || p.EmployeeId == employeeId)
                orderby p.PunchedAt
                select new PunchDto(p.Id, p.EmployeeId, e != null ? e.FullName : null, p.Pin, p.PunchedAt, p.Direction, p.VerifyMode, p.Source, d != null ? d.Name : null))
            .Take(5000)
            .ToListAsync(ct);
        return TypedResults.Ok(punches);
    }

    /// <summary>PINs seen on terminals but not assigned to any employee — usually an enrolment typo to fix.</summary>
    private static async Task<IResult> UnmatchedPins(HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<AttendancePunch>().AsNoTracking()
            .Where(p => p.EmployeeId == null)
            .GroupBy(p => p.Pin)
            .Select(g => new UnmatchedPinDto(g.Key, g.Count(), g.Min(p => p.PunchedAt), g.Max(p => p.PunchedAt)))
            .OrderByDescending(p => p.PunchCount)
            .Take(200)
            .ToListAsync(ct));

    private static async Task<IResult> ListRegularisations([AsParameters] PageQuery query, ApprovalStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var requests = db.Set<AttendanceRegularisation>().AsNoTracking();
        if (status is not null)
        {
            requests = requests.Where(r => r.Status == status);
        }

        return TypedResults.Ok(await ProjectRegularisations(requests, db).ToPagedResultAsync(page, ct));
    }

    internal static IQueryable<RegularisationDto> ProjectRegularisations(IQueryable<AttendanceRegularisation> requests, HospitalDbContext db) =>
        from r in requests
        join e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.EmployeeId equals e.Id
        orderby r.Status == ApprovalStatus.Pending descending, r.Date descending
        select new RegularisationDto(r.Id, r.EmployeeId, e.EmployeeCode, e.FullName, r.Date, r.Kind, r.RequestedIn, r.RequestedOut, r.Reason, r.Status,
            r.CreatedAt, r.DecidedAt, r.DecisionNote);
}
