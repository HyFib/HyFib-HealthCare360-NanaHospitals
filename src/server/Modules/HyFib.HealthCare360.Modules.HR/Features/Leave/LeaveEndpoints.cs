using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.Leave;

/// <summary>/api/v1/hr/leave and /api/v1/hr/holidays — leave policy, balances, requests and the holiday calendar (HR-05).</summary>
internal static class LeaveEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr").WithTags("HR");

        group.MapGet("/leave/types", ListTypes).RequireAnyPermission(HRPermissions.Self, HRPermissions.LeaveApprove, HRPermissions.EmployeesRead);
        group.MapPost("/leave/types", (SaveLeaveTypeRequest r, HospitalDbContext db, CancellationToken ct) => SaveType(null, r, db, ct))
            .RequirePermission(HRPermissions.EmployeesManage).WithValidation<SaveLeaveTypeRequest>();
        group.MapPut("/leave/types/{id:guid}", (Guid id, SaveLeaveTypeRequest r, HospitalDbContext db, CancellationToken ct) => SaveType(id, r, db, ct))
            .RequirePermission(HRPermissions.EmployeesManage).WithValidation<SaveLeaveTypeRequest>();

        group.MapGet("/holidays", ListHolidays).RequireAnyPermission(HRPermissions.Self, HRPermissions.AttendanceManage, HRPermissions.LeaveApprove);
        group.MapPost("/holidays", AddHoliday).RequirePermission(HRPermissions.AttendanceManage).WithValidation<SaveHolidayRequest>();
        group.MapDelete("/holidays/{id:guid}", DeleteHoliday).RequirePermission(HRPermissions.AttendanceManage);

        group.MapGet("/leave/balances", Balances).RequireAnyPermission(HRPermissions.LeaveApprove, HRPermissions.EmployeesRead);
        group.MapPost("/leave/balances/credit", async (CreditLeaveRequest r, LeaveService leave, CancellationToken ct) => (await leave.CreditYearAsync(r.Year, ct)).ToOk())
            .RequirePermission(HRPermissions.LeaveApprove).WithValidation<CreditLeaveRequest>();
        group.MapPost("/leave/balances/adjust", async (AdjustLeaveBalanceRequest r, LeaveService leave, CancellationToken ct) => (await leave.AdjustAsync(r, ct)).ToNoContent())
            .RequirePermission(HRPermissions.LeaveApprove).WithValidation<AdjustLeaveBalanceRequest>();

        group.MapGet("/leave/requests", ListRequests).RequirePermission(HRPermissions.LeaveApprove);
        group.MapPost("/employees/{employeeId:guid}/leave-requests", async (Guid employeeId, ApplyLeaveRequest r, LeaveService leave, CancellationToken ct) =>
                (await leave.ApplyAsync(employeeId, r, ct)).ToCreated(id => $"/api/v1/hr/leave/requests/{id}"))
            .RequirePermission(HRPermissions.LeaveApprove).WithValidation<ApplyLeaveRequest>();
        group.MapPost("/leave/requests/{id:guid}/approve", async (Guid id, DecisionRequest r, LeaveService leave, CancellationToken ct) =>
                (await leave.DecideAsync(id, approve: true, r.Note, ct)).ToNoContent())
            .RequirePermission(HRPermissions.LeaveApprove).WithValidation<DecisionRequest>();
        group.MapPost("/leave/requests/{id:guid}/reject", async (Guid id, DecisionRequest r, LeaveService leave, CancellationToken ct) =>
                (await leave.DecideAsync(id, approve: false, r.Note, ct)).ToNoContent())
            .RequirePermission(HRPermissions.LeaveApprove).WithValidation<DecisionRequest>();
        group.MapPost("/leave/requests/{id:guid}/cancel", async (Guid id, CancelLeaveRequest r, LeaveService leave, CancellationToken ct) =>
                (await leave.CancelAsync(id, r.Reason, ownEmployeeId: null, ct)).ToNoContent())
            .RequirePermission(HRPermissions.LeaveApprove).WithValidation<CancelLeaveRequest>();
    }

    private static async Task<IResult> ListTypes(bool? includeInactive, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<LeaveType>().AsNoTracking()
            .Where(t => includeInactive == true || t.IsActive)
            .OrderBy(t => t.Code)
            .Select(t => new LeaveTypeDto(t.Id, t.Code, t.Name, t.AnnualQuota, t.CarryForwardCap, t.IsEncashable, t.IsPaid, t.AllowHalfDay, t.TracksBalance,
                t.CountsNonWorkingDays, t.MaxDaysPerRequest, t.DocumentRequiredAfterDays, t.ApplicableGender, t.IsActive, t.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveType(Guid? id, SaveLeaveTypeRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<LeaveType>().AnyAsync(t => t.Code == code && t.Id != id, ct))
        {
            return Error.Conflict("leave_type.code_in_use", $"Leave code {code} is already used.").ToProblem();
        }

        LeaveType type;
        if (id is { } existingId)
        {
            var found = await db.Set<LeaveType>().FirstOrDefaultAsync(t => t.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(LeaveType), existingId).ToProblem();
            }

            type = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(type, version);
            }
        }
        else
        {
            type = new LeaveType();
            db.Add(type);
        }

        type.Code = code;
        type.Name = request.Name.Trim();
        type.AnnualQuota = request.AnnualQuota;
        type.CarryForwardCap = request.CarryForwardCap;
        type.IsEncashable = request.IsEncashable;
        type.IsPaid = request.IsPaid;
        type.AllowHalfDay = request.AllowHalfDay;
        type.TracksBalance = request.TracksBalance;
        type.CountsNonWorkingDays = request.CountsNonWorkingDays;
        type.MaxDaysPerRequest = request.MaxDaysPerRequest;
        type.DocumentRequiredAfterDays = request.DocumentRequiredAfterDays;
        type.ApplicableGender = request.ApplicableGender;
        type.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/hr/leave/types/{type.Id}", new { type.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> ListHolidays(int? year, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var y = year ?? time.LocalToday().Year;
        var from = new DateOnly(y, 1, 1);
        var to = new DateOnly(y, 12, 31);
        return TypedResults.Ok(await db.Set<Holiday>().AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .OrderBy(h => h.Date)
            .Select(h => new HolidayDto(h.Id, h.Date, h.Name, h.Kind))
            .ToListAsync(ct));
    }

    private static async Task<IResult> AddHoliday(SaveHolidayRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        if (await db.Set<Holiday>().AnyAsync(h => h.Date == request.Date, ct))
        {
            return Error.Conflict("holiday.exists", $"{request.Date:dd MMM yyyy} is already a holiday.").ToProblem();
        }

        var holiday = new Holiday { FacilityId = facilityId, Date = request.Date, Name = request.Name.Trim(), Kind = request.Kind };
        db.Add(holiday);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/hr/holidays/{holiday.Id}", new { holiday.Id });
    }

    private static async Task<IResult> DeleteHoliday(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var holiday = await db.Set<Holiday>().FirstOrDefaultAsync(h => h.Id == id, ct);
        if (holiday is null)
        {
            return Error.NotFound(nameof(Holiday), id).ToProblem();
        }

        db.Remove(holiday);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Balances(Guid employeeId, int? year, HospitalDbContext db, LeaveService leave, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Set<Employee>().AnyAsync(e => e.Id == employeeId, ct))
        {
            return Error.NotFound(nameof(Employee), employeeId).ToProblem();
        }

        return TypedResults.Ok(await leave.BalancesAsync(employeeId, year ?? time.LocalToday().Year, ct));
    }

    private static async Task<IResult> ListRequests(
        [AsParameters] PageQuery query,
        ApprovalStatus? status,
        Guid? employeeId,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var requests = db.Set<LeaveRequest>().AsNoTracking();
        if (status is not null)
        {
            requests = requests.Where(l => l.Status == status);
        }

        if (employeeId is not null)
        {
            requests = requests.Where(l => l.EmployeeId == employeeId);
        }

        if (from is not null)
        {
            requests = requests.Where(l => l.ToDate >= from);
        }

        if (to is not null)
        {
            requests = requests.Where(l => l.FromDate <= to);
        }

        return TypedResults.Ok(await Project(requests, db).ToPagedResultAsync(page, ct));
    }

    /// <summary>Shared projection for HR, team and self-service lists (pending first, then newest).</summary>
    internal static IQueryable<LeaveRequestDto> Project(IQueryable<LeaveRequest> requests, HospitalDbContext db) =>
        from l in requests
        join e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on l.EmployeeId equals e.Id
        join t in db.Set<LeaveType>() on l.LeaveTypeId equals t.Id
        orderby l.Status == ApprovalStatus.Pending descending, l.AppliedAt descending
        select new LeaveRequestDto(l.Id, l.EmployeeId, e.EmployeeCode, e.FullName, l.LeaveTypeId, t.Code, l.FromDate, l.FromSession, l.ToDate,
            l.ToSession, l.Days, l.Reason, l.Status, l.AppliedAt, l.DecidedAt, l.DecisionNote, l.AttachmentFileId);
}
